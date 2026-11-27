# INSTALLER-15: groups through the token (work in progress)

## Built so far

- Nothing yet: audit done, design below.

## Plan

- Join request: a Gateway reports its pinned listeners; an instance that already holds its group's key sends it (adoption, see the defaults).
- Join answer: the group key, the group's listeners (a further Gateway instance only), a link slot (null until TRUSTZONES-1).
- Console: `IJoinGroups` in `JoinService`; the group key is kept by instance id as `ConsoleGroupKeys` keeps it; a group name is tied to the
  first instance id that joined with it.
- Gateway: with a join request waiting and no key held, the instance neither makes a key nor serves the group certificate; after the join it keeps
  the key, writes the group's ports to `config/server.yaml` and starts again in the same process.
- Runner: keeps the key from the join answer.

## Open questions

## Defaults taken
