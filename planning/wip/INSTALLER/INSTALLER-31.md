# INSTALLER-31 (work in progress)

## Built

- `DbDataKit/Dockerfile`: listens on 7800 (was 8480), `--public-url https://localhost:7800`, `HEALTHCHECK` a bare `health check`.
- `DbDataGateway/Dockerfile`: Flight SQL on 7821 (was 8815), management on 127.0.0.1:7822 (was 8816), a new `HEALTHCHECK` with a bare `health check`.
- `dist/compose/*.yml` and `dist/compose/quickstart/*.sh`, `docs/quickstart.md`, `docs/apps-testing.md`, `docs/suite-listeners-and-auth.md`,
  `docs/install.md`, `DbDataGateway/README.md`, the `image` job of `dbdatakit-ci.yml`, `dist/tests/test_dist.py` and `ListenerPinningTests` follow.
- Release notes entry; a row in `docs/ci-lessons.md`.

## Defaults taken

- D1: the ports the images and compose files take inside the container: console 7800, the console's announce port 7801, the Gateway's Flight SQL 7821
  and management 7822 (the order a bare Gateway takes: public 7820, Flight 7821, management 7822), the identity home 7823 and the apps host 7824 in
  `quickstart-apps.yml`. Example: `docker compose -f dist/compose/quickstart.yml up` publishes `7800:7800` and `7821:7821`; a Flight client connects to
  `localhost:7821`.
- D2: the host side of each `ports:` entry moves too (`"7800:7800"`), so the address a person opens is the same as on an install.
- D3: the demo PostgreSQL keeps `15434` on the host (it is not a suite service). It is also the port `testenv` gives `postgres16`, so the two cannot
  run at once on one machine.
- D4: the Gateway image's public listener is left unset, as before: inside the container it takes the first free port of the block (7820) and is not
  published.
