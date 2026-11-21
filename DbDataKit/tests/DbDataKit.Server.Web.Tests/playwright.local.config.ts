import base from './playwright.config'
const exe = '/opt/pw-browsers/chromium_headless_shell-1243/chrome-headless-shell-linux64/chrome-headless-shell'
export default { ...base, webServer: (base.webServer as any[]).slice(0, 1), projects: base.projects!.map((p) => ({ ...p, use: { ...p.use, launchOptions: { executablePath: exe } } })) }
