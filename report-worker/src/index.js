const DEFAULT_OWNER = "RandomLabdDev";
const DEFAULT_REPOSITORY = "PcTemp-Reports";
const MAX_BODY_BYTES = 48 * 1024;
const MAX_SENSORS = 32;

const responseHeaders = {
  "content-type": "application/json; charset=utf-8",
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

export default {
  async fetch(request, env) {
    try {
      return await handleRequest(request, env);
    } catch (error) {
      console.error("Unhandled report error", error?.message ?? "unknown");
      return jsonResponse(500, { ok: false, error: "internal_error" });
    }
  },
};

export async function handleRequest(request, env) {
  const url = new URL(request.url);

  if (request.method === "GET" && (url.pathname === "/" || url.pathname === "/health")) {
    return jsonResponse(200, {
      ok: true,
      service: "PcTemp Reports",
      schema: 1,
    });
  }

  if (request.method !== "POST" || url.pathname !== "/v1/report") {
    return jsonResponse(404, { ok: false, error: "not_found" });
  }

  const contentType = request.headers.get("content-type") ?? "";
  if (!contentType.toLowerCase().startsWith("application/json")) {
    return jsonResponse(415, { ok: false, error: "json_required" });
  }

  const declaredLength = Number(request.headers.get("content-length") ?? 0);
  if (Number.isFinite(declaredLength) && declaredLength > MAX_BODY_BYTES) {
    return jsonResponse(413, { ok: false, error: "report_too_large" });
  }

  const bytes = await request.arrayBuffer();
  if (bytes.byteLength > MAX_BODY_BYTES) {
    return jsonResponse(413, { ok: false, error: "report_too_large" });
  }

  let raw;
  try {
    raw = JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    return jsonResponse(400, { ok: false, error: "invalid_json" });
  }

  const validation = validateReport(raw);
  if (!validation.ok) {
    return jsonResponse(400, { ok: false, error: validation.error });
  }

  const report = validation.report;
  if (env.REPORT_LIMITER?.limit) {
    const ip = request.headers.get("cf-connecting-ip") ?? "unknown";
    const ipKey = await sha256(`ip:${ip}`);
    const installationKey = await sha256(`installation:${report.installationId}`);
    const [ipRate, installationRate] = await Promise.all([
      env.REPORT_LIMITER.limit({ key: ipKey }),
      env.REPORT_LIMITER.limit({ key: installationKey }),
    ]);
    if (!ipRate.success || !installationRate.success) {
      return jsonResponse(429, { ok: false, error: "rate_limited" });
    }
  }

  if (!env.GITHUB_TOKEN) {
    console.error("GITHUB_TOKEN is not configured");
    return jsonResponse(503, { ok: false, error: "service_unavailable" });
  }

  const owner = env.GITHUB_OWNER || DEFAULT_OWNER;
  const repository = env.GITHUB_REPOSITORY || DEFAULT_REPOSITORY;
  const issue = buildIssue(report);
  const githubResponse = await fetch(
    `https://api.github.com/repos/${encodeURIComponent(owner)}/${encodeURIComponent(repository)}/issues`,
    {
      method: "POST",
      headers: {
        accept: "application/vnd.github+json",
        authorization: `Bearer ${env.GITHUB_TOKEN}`,
        "content-type": "application/json",
        "user-agent": "PcTemp-Reports-Worker",
        "x-github-api-version": "2026-03-10",
      },
      body: JSON.stringify(issue),
    },
  );

  if (!githubResponse.ok) {
    console.error("GitHub issue creation failed", githubResponse.status);
    return jsonResponse(502, { ok: false, error: "report_delivery_failed" });
  }

  return jsonResponse(202, {
    ok: true,
    accepted: true,
    reportId: report.reportId,
  });
}

export function validateReport(raw) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    return failure("invalid_report");
  }
  if (raw.schema !== 1) return failure("unsupported_schema");
  if (raw.consent !== true) return failure("consent_required");

  const reportId = clip(raw.reportId, 64);
  const installationId = clip(raw.installationId, 64);
  const appVersion = clip(raw.appVersion, 32);
  const exceptionType = clip(raw.exception?.type, 160);
  const exceptionMessage = clip(raw.exception?.message, 4000);

  if (!isUuid(reportId) || !isUuid(installationId)) return failure("invalid_identifier");
  if (!appVersion || !exceptionType || !exceptionMessage) return failure("missing_required_field");

  const timestamp = normalizeTimestamp(raw.timestampUtc);
  if (!timestamp) return failure("invalid_timestamp");

  const sensors = Array.isArray(raw.sensors)
    ? raw.sensors.slice(0, MAX_SENSORS).map(normalizeSensor).filter(Boolean)
    : [];

  return {
    ok: true,
    report: {
      reportId,
      installationId,
      appVersion,
      timestampUtc: timestamp,
      osVersion: clip(raw.system?.osVersion, 160),
      architecture: clip(raw.system?.architecture, 32),
      culture: clip(raw.system?.culture, 32),
      cpu: clip(raw.hardware?.cpu, 240),
      gpu: clip(raw.hardware?.gpu, 240),
      motherboard: clip(raw.hardware?.motherboard, 240),
      memory: clip(raw.hardware?.memory, 240),
      exceptionType,
      exceptionMessage,
      stackTrace: clip(raw.exception?.stackTrace, 12000, true),
      component: clip(raw.context?.component, 120),
      action: clip(raw.context?.action, 160),
      sensors,
    },
  };
}

export function buildIssue(report) {
  const title = `[Informe automático] PcTemp ${report.appVersion} · ${safeInline(report.exceptionType)}`;
  const body = [
    "## Informe automático de PcTemp",
    "",
    "> Generado con el consentimiento del usuario. Los campos recibidos se han limitado a una lista segura.",
    "",
    "| Campo | Valor |",
    "|---|---|",
    `| Versión | ${safeInline(report.appVersion)} |`,
    `| Fecha UTC | ${safeInline(report.timestampUtc)} |`,
    `| Sistema | ${safeInline(report.osVersion || "No disponible")} |`,
    `| Arquitectura | ${safeInline(report.architecture || "No disponible")} |`,
    `| Componente | ${safeInline(report.component || "No disponible")} |`,
    `| Acción | ${safeInline(report.action || "No disponible")} |`,
    `| ID del informe | ${safeInline(report.reportId)} |`,
    "",
    "### Error",
    "",
    `**${safeInline(report.exceptionType)}**`,
    "",
    codeBlock(report.exceptionMessage),
    "",
    "### Hardware comunicado",
    "",
    `- CPU: ${safeInline(report.cpu || "No disponible")}`,
    `- GPU: ${safeInline(report.gpu || "No disponible")}`,
    `- Placa base: ${safeInline(report.motherboard || "No disponible")}`,
    `- Memoria: ${safeInline(report.memory || "No disponible")}`,
  ];

  if (report.sensors.length > 0) {
    body.push("", "### Lecturas recientes", "", "| Componente | Sensor | Valor |", "|---|---|---|");
    for (const sensor of report.sensors) {
      body.push(
        `| ${safeInline(sensor.component)} | ${safeInline(sensor.name)} | ${safeInline(sensor.value)} ${safeInline(sensor.unit)} |`,
      );
    }
  }

  if (report.stackTrace) {
    body.push("", "<details>", "<summary>Traza anonimizada</summary>", "", codeBlock(report.stackTrace), "", "</details>");
  }

  return { title: title.slice(0, 240), body: body.join("\n") };
}

function normalizeSensor(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const component = clip(value.component, 80);
  const name = clip(value.name, 120);
  const unit = clip(value.unit, 12);
  const numericValue = Number(value.value);
  if (!component || !name || !unit || !Number.isFinite(numericValue)) return null;
  return { component, name, unit, value: numericValue.toFixed(1) };
}

function normalizeTimestamp(value) {
  const text = clip(value, 64);
  if (!text) return "";
  const timestamp = Date.parse(text);
  return Number.isFinite(timestamp) ? new Date(timestamp).toISOString() : "";
}

function clip(value, maxLength, preserveLines = false) {
  if (typeof value !== "string") return "";
  let text = value.replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, "").trim();
  if (!preserveLines) text = text.replace(/[\r\n]+/g, " ");
  return text.slice(0, maxLength);
}

function safeInline(value) {
  return String(value ?? "")
    .replace(/\\/g, "\\\\")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/\|/g, "\\|")
    .replace(/([\[\]()])/g, "\\$1")
    .replace(/@/g, "@\u200b")
    .replace(/[\r\n]+/g, " ")
    .trim();
}

function codeBlock(value) {
  const safe = String(value ?? "").replace(/```/g, "`\u200b``");
  return `\`\`\`text\n${safe}\n\`\`\``;
}

function isUuid(value) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}

function failure(error) {
  return { ok: false, error };
}

function jsonResponse(status, payload) {
  return new Response(JSON.stringify(payload), { status, headers: responseHeaders });
}

async function sha256(value) {
  const bytes = new TextEncoder().encode(value);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
}
