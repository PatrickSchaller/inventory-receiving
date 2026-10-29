# API explorer assets

Swagger UI **5.34.0** is vendored from the [official release](https://github.com/swagger-api/swagger-ui/releases/tag/v5.34.0), using `dist/swagger-ui-bundle.js` and `dist/swagger-ui.css`. Its Apache 2.0 [license](vendor/LICENSE.txt) and [notice](vendor/NOTICE.txt) are included. Vendor bundles are unmodified; no CDN or online validator is used.

`reference.js` enables request execution only when served by the local app at `http://127.0.0.1:5087`. The portfolio-hosted copy permits browsing operations and schemas without running a public backend. Read-only presentation is not an authentication mechanism.

The local and published explorer use the same files. Local requests load the runtime-generated contract at `/openapi/v1.json`; the static portfolio uses the checked-in `openapi.json`. A test compares that snapshot with the actual generated contract to detect drift.
