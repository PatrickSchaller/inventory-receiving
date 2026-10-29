import { defineConfig, devices } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import path from 'node:path'

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  reporter: 'list',
  use: { baseURL: 'http://127.0.0.1:5187', trace: 'retain-on-failure', timezoneId: 'America/Chicago' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1050 } } }],
  webServer: [
    {
      command: 'dotnet bin/Debug/net10.0/Receiving.Api.dll',
      cwd: path.resolve('../src/Receiving.Api'),
      url: 'http://127.0.0.1:5087/api/items',
      env: { Inventory__DatabasePath: path.resolve('../src/Receiving.Api/data', `ui-${randomUUID()}.db`) },
      timeout: 60_000,
    },
    { command: 'npm run dev', url: 'http://127.0.0.1:5187', timeout: 30_000 },
  ],
})
