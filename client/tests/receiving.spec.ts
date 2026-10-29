import { expect, test } from '@playwright/test'
import path from 'node:path'

test('receiving, replay, conflict, correction, export, and a mobile layout', async ({ page }) => {
  const pageErrors: string[] = []
  page.on('pageerror', error => pageErrors.push(error.message))
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Receiving desk' })).toBeVisible()
  const itemRow = page.getByRole('row').filter({ has: page.getByRole('button', { name: 'Brass cabinet handle HW-104' }) })
  await expect(itemRow).toContainText('10')
  await page.getByLabel('Receipt reference').fill('DEMO-1048')
  await page.getByRole('spinbutton', { name: 'Receipt quantity 1' }).fill('5')
  await page.getByRole('button', { name: 'Record receipt', exact: true }).click()
  await expect(page.getByRole('status')).toHaveText('Receipt DEMO-1048 recorded.')
  await expect(itemRow).toContainText('15')
  await expect(page.getByRole('cell', { name: 'DEMO-1048', exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Record receipt', exact: true }).click()
  await expect(page.getByRole('status')).toContainText('Stock was not added again.')
  await expect(itemRow).toContainText('15')

  await page.getByRole('spinbutton', { name: 'Receipt quantity 1' }).fill('6')
  await page.getByRole('button', { name: 'Record receipt', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('already used with different items or quantities')
  await expect(itemRow).toContainText('15')

  await page.getByRole('button', { name: 'Adjust stock', exact: true }).click()
  await page.getByLabel('Quantity change').fill('-2')
  await page.getByLabel('Reason', { exact: true }).fill('Two handles damaged during handling')
  await page.getByRole('button', { name: 'Record adjustment' }).click()
  await expect(page.getByRole('status')).toContainText('Stock adjustment recorded.')
  await expect(itemRow).toContainText('13')
  await expect(page.getByRole('cell', { name: 'Two handles damaged during handling', exact: true })).toBeVisible()

  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('link', { name: 'Export CSV' }).click()
  expect((await downloadPromise).suggestedFilename()).toBe('stock-movements.csv')
  await page.getByRole('button', { name: 'Receive stock', exact: true }).click()
  await page.getByRole('spinbutton', { name: 'Receipt quantity 1' }).fill('5')
  await page.getByLabel('Receipt reference').fill('DEMO-1049')
  await page.screenshot({ path: path.resolve('../docs/receiving-desk.png'), fullPage: true })

  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.getByRole('heading', { name: 'Receiving desk' })).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.screenshot({ path: path.resolve('../docs/receiving-mobile.png'), fullPage: true })
  expect(pageErrors).toEqual([])
})

test('connection failure gives the user a recovery action', async ({ page }) => {
  await page.route('**/api/items', route => route.abort())
  await page.goto('/')
  await expect(page.getByRole('alert')).toContainText('Check that the API is running, then refresh stock.')
  await expect(page.getByRole('button', { name: 'Record receipt', exact: true })).toBeDisabled()
})
