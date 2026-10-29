import { expect, test } from '@playwright/test'
import path from 'node:path'

test('local API explorer executes documented requests with the application CSP', async ({ page }) => {
  const problems: string[] = []
  page.on('pageerror', error => problems.push(error.message))
  page.on('console', message => { if (message.type() === 'error') problems.push(message.text()) })
  await page.goto('http://127.0.0.1:5087/api-docs/index.html')
  await expect(page.getByRole('status')).toContainText('Local explorer')
  await expect(page.locator('.opblock')).toHaveCount(5)
  const inventory = page.locator('#operations-Inventory-ListItems')
  await inventory.locator('.opblock-summary-control').click()
  await inventory.getByRole('button', { name: 'Try it out' }).click()
  const items = page.waitForResponse(response => response.url().endsWith('/api/items') && response.request().method() === 'GET')
  await inventory.getByRole('button', { name: 'Execute', exact: true }).click()
  expect((await items).status()).toBe(200)
  await expect(inventory.locator('.live-responses-table')).toContainText('HW-104')

  const receipt = page.locator('#operations-Receiving-ReceiveStock')
  await receipt.locator('.opblock-summary-control').click()
  await receipt.getByRole('button', { name: 'Try it out' }).click()
  await receipt.locator('textarea').fill(JSON.stringify({ reference: 'BROWSER-API-DOCS-001', lines: [{ sku: 'WD-310', quantity: 1 }] }))
  const saved = page.waitForResponse(response => response.url().endsWith('/api/receipts') && response.request().method() === 'POST')
  await receipt.getByRole('button', { name: 'Execute', exact: true }).click()
  const response = await saved
  expect(response.status()).toBe(200)
  expect(response.request().headers()['x-inventory-client']).toBe('stockroom')
  await expect(receipt.locator('.live-responses-table')).toContainText('BROWSER-API-DOCS-001')
  await receipt.locator('.live-responses-table').screenshot({ path: path.resolve('../docs/api-explorer.png') })
  await page.setViewportSize({ width: 390, height: 844 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  expect(problems).toEqual([])
})
