import { test } from '@playwright/test'
import { signInNewAdmin } from './people'
import { instanceEditorStory, publishStory } from './gateway-apps-editors-screen'

// Plan 33, UE01 to UE03: the editors of the Gateway's Apps screen (an instance, a published version, a navigation set), with the Gateway's management routes mocked in the page, so the story needs the signed-in console only
// and takes no port (the Gateway side is DbDataGateway.Tests' AppsScreenTests; the real Gateway is `gateway-apps-editors.spec.ts`). Each test signs in a new administrator, so the file sorts after `identity-home` and `first-run`.
test('an instance is made and changed from the Apps screen: the form offers the models of the version, sends what was typed, shows the Gateway\'s refusal and keeps settings when a pin changes', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Instance Tester', `gateway-apps-instance-${Date.now()}@example.com`)
  await instanceEditorStory(page)
})

test('a version is published from a folder: checked first, sent as files, a trusted app approved by someone else with the origins chosen, and the Gateway\'s refusals shown', async ({ page, context, baseURL }) => {
  await signInNewAdmin(page, context, baseURL!, 'Publish Tester', `gateway-apps-publish-${Date.now()}@example.com`)
  await publishStory(page)
})
