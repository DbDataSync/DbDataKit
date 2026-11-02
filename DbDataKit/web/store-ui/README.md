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

`npm test -w @dbdatakit/store-ui` runs the tests (field parsing and the change a form makes, the client against a fake fetcher, the views rendered to markup). The containers that hold state (`EntityGrid`, `RowForm`, `EntityEditor`) need a browser to test and are covered by the console's Playwright suite when it gets a Store server (not yet).
