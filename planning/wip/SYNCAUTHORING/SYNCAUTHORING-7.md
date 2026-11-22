# SYNCAUTHORING-7 (work in progress)

## Built so far

Nothing yet: the branch is started from origin/main.

## Plan

- Move to `Shared/src/DbDataKit.Sync.*`: Core (the configuration model), Drivers.Abstractions (`DriverRegistry`), Scripting.Abstractions
  (`ScriptSlots`), Drivers.Generic (what Provisioning and the descriptors need), Provisioning (`MappingColumnReader`), and the part of
  Drivers.Descriptor that reads a `driver.yaml` (not `DriverScriptHost`, which compiles and runs `driver.csx`, nor what loads a library).
- The old projects stay as forwarders (`[assembly: TypeForwardedTo]`), namespaces unchanged, so DbDataSync's tests compile unchanged.

## Open questions

## Defaults taken
