# @dbdatakit/store-ui

Reusable controls for DbDataStore data (plan 19, Z06): a grid of an entity's rows, a form made from the model, a row's history, and `EntityEditor`, which puts them together. They are written to be mounted by **the console and by apps** alike, so they know nothing of either:

* They reach the Store only through a `StoreClient`. `createStoreClient(fetcher)` builds one from a `fetch`-shaped function that the host supplies and that already carries who is asking: the console's sends the call through its relay as the signed-in person; an app's adds the app's own scoped token (plan 16 section 2). Put it in a `StoreClientProvider`.
* No router, no shell, no query cache, no global state. A host with its own cache can use the presentational `GridView`, `FormView`, `FieldView`, `HistoryView` and `ViolationList` (props in, markup out) and the client's methods directly.
* The look is `styles.css`; every colour is a `--sui-*` variable a host sets on a parent to match itself, and the defaults follow the system's light or dark setting.
* **The server is the authority.** A control chooses an input from the attribute's type, shows hints (required, length, range) and parses what was typed; every rule is checked by the Store, and its refusals (422) are shown against the attribute they name, a stale version (409) tells the person to reload the row. Nothing here is a place to enforce a rule.

```tsx
import { StoreClientProvider, EntityEditor, createStoreClient } from '@dbdatakit/store-ui'
import '@dbdatakit/store-ui/styles.css'

const client = createStoreClient((path, init) => fetch(`${storeBase}${path}`, { ...init, headers: { ...init?.headers, Authorization: `Bearer ${appToken}` } }))

<StoreClientProvider client={client}><EntityEditor model="territories" entity="territory" /></StoreClientProvider>
```

**Instances (plan 26 section 6.2).** A model that keeps instances apart (`schema`, `connection` or `column`) is shown one instance at a time: `client.withInstance('acme')` is the same client reading and writing that instance (`null` or `'default'` is the model's own copy), `client.instances(model)` lists them, `InstanceScope` scopes everything below it, and `<EntityEditor instance={...} onInstance={...} />` draws an `InstancePicker` (nothing for a model with one copy). The server does the isolation; the control only picks which instance's path to call.

`npm test -w @dbdatakit/store-ui` runs the tests (field parsing and the change a form makes, the client against a fake fetcher, the views rendered to markup). The containers that hold state (`EntityGrid`, `RowForm`, `EntityEditor`) need a browser to test and are covered by the console's Playwright stories `store-entity.spec.ts`, `store-models.spec.ts` and `store-changes.spec.ts` (the Store's side mocked in the page). Every control has a `data-testid` (`sui-grid`, `sui-row`, `sui-field-<attribute>`, `sui-problem-<attribute>`, `sui-submit`, `sui-delete`, `sui-history`, `sui-instance-select`, ...).
