const live = location.protocol === 'http:' && location.hostname === '127.0.0.1' && location.port === '5087';
const contract = live ? '/openapi/v1.json' : 'openapi.json';
document.querySelector('#openapi-link').href = contract;
if (live) {
  document.querySelector('#reference-mode').textContent = 'Local explorer: “Try it out” sends real requests to your local demo database. POST operations change that database. Use a scratch database for the ordered HTTP walkthrough.';
}

window.SwaggerUIBundle({
  url: contract,
  dom_id: '#swagger-ui',
  presets: [window.SwaggerUIBundle.presets.apis],
  layout: 'BaseLayout',
  deepLinking: true,
  defaultModelsExpandDepth: 0,
  docExpansion: 'list',
  supportedSubmitMethods: live ? ['get', 'post'] : [],
  validatorUrl: null,
  queryConfigEnabled: false,
  persistAuthorization: false
});
