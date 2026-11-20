# ENDPOINTS-1 (work in progress)

## Built so far

- `PublicListener.cs`: the public listener (`public:` section, address and port), `PublicMounts`, `InProcessServer`, `ModuleListener`.
- The six modules (identity, apps, store, views, feeds, mcp): `port` is optional; with no port the module's web application is forwarded to by the public listener.
- Validator: one port-clash check for all listeners.

## Not built yet

- Tests (one process, one port), docs, check_default_ports.py, settings form hint for `rest.port`.

## Defaults taken

- Own listener = the module's existing `port` (and `address`) setting; no new `listen:` key. Example: `store: { port: 9443 }` puts the store on 9443 and off `/store/`.
- The public listener's default port is 8817 until ENDPOINTS-2 pins one.
- Identity keeps the paths it has on the public listener (`/.well-known/openid-configuration`, `/identity/...`, `/invite`, `/api/auth/...`, `/api/invites`, `/api/users`), apps keep `/apps/...` and every path of a host name bound to an instance; the other four are under `/store`, `/views`, `/feeds`, `/mcp` with the prefix removed for the module.
