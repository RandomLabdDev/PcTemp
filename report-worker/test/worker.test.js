import assert from "node:assert/strict";
import test from "node:test";

import { buildIssue, handleRequest, validateReport } from "../src/index.js";

const validReport = {
  schema: 1,
  consent: true,
  reportId: "2fe2cd62-5966-4a43-a92d-cd09ea1e5e4a",
  installationId: "ac15430e-82be-4f47-8b4d-a93898e4d0c7",
  appVersion: "1.13.59",
  timestampUtc: "2026-07-21T20:00:00Z",
  system: { osVersion: "Windows 11", architecture: "x64", culture: "es-ES" },
  hardware: { cpu: "Intel", gpu: "NVIDIA", motherboard: "ASUS", memory: "32 GB" },
  exception: { type: "InvalidOperationException", message: "Fallo de prueba", stackTrace: "at PcTemp.Test()" },
  context: { component: "Sensors", action: "Refresh" },
  sensors: [{ component: "CPU", name: "Package", value: 54.25, unit: "°C" }],
};

test("health endpoint does not require GitHub", async () => {
  const response = await handleRequest(new Request("https://example.test/health"), {});
  assert.equal(response.status, 200);
  assert.equal((await response.json()).service, "PcTemp Reports");
});

test("validates and normalizes an accepted report", () => {
  const result = validateReport(validReport);
  assert.equal(result.ok, true);
  assert.equal(result.report.sensors[0].value, "54.3");
});

test("requires explicit consent", () => {
  const result = validateReport({ ...validReport, consent: false });
  assert.deepEqual(result, { ok: false, error: "consent_required" });
});

test("does not copy unknown or sensitive fields", () => {
  const result = validateReport({ ...validReport, username: "secret", serialNumber: "1234" });
  assert.equal(result.ok, true);
  assert.equal("username" in result.report, false);
  assert.equal("serialNumber" in result.report, false);
});

test("builds a private issue without the installation identifier", () => {
  const report = validateReport(validReport).report;
  const issue = buildIssue(report);
  assert.match(issue.title, /PcTemp 1\.13\.59/);
  assert.doesNotMatch(issue.body, new RegExp(validReport.installationId));
  assert.match(issue.body, /CPU/);
});

test("neutralizes markup and mentions in issue fields", () => {
  const result = validateReport({
    ...validReport,
    exception: { ...validReport.exception, type: "<img src=x> @admin" },
  });
  const issue = buildIssue(result.report);
  assert.doesNotMatch(issue.title, /<img/);
  assert.doesNotMatch(issue.title, /@admin/);
  assert.match(issue.title, /&lt;img/);
});

test("rejects oversized reports", async () => {
  const request = new Request("https://example.test/v1/report", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ ...validReport, padding: "x".repeat(50 * 1024) }),
  });
  const response = await handleRequest(request, {});
  assert.equal(response.status, 413);
});
