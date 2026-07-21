# PcTemp Reports Worker

Intermediario de recepción de informes anónimos de PcTemp. El Worker valida una lista cerrada de campos y crea una incidencia en el repositorio privado `PcTemp-Reports`. El token de GitHub se almacena exclusivamente como secreto de Cloudflare.

## Endpoints

- `GET /health`: estado del servicio.
- `POST /v1/report`: recepción de informes con esquema 1.

El cuerpo máximo aceptado es de 48 KiB. Se exige consentimiento explícito, dos identificadores UUID aleatorios y los datos mínimos del error. Los campos no reconocidos se descartan.

## Configuración

```powershell
npm install
npx wrangler secret put GITHUB_TOKEN
npm test
npm run deploy
```

El token de GitHub debe estar limitado únicamente a `PcTemp-Reports`, con `Metadata: read` e `Issues: read and write`.

Para desarrollo local, el secreto puede guardarse en `.dev.vars`, que está excluido de Git:

```text
GITHUB_TOKEN=github_pat_...
```

No se debe incorporar el token a PcTemp, al instalador, a `wrangler.jsonc` ni al repositorio.
