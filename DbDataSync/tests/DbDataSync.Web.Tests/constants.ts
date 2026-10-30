// Values the suite's specs share with the Playwright configs. They live here, not in a config, because a config starts servers and clears directories when it is loaded:
// a spec that imports one would run that again in whichever process lists the tests.

// The one bundled KnownDrivers catalog entry (phase 117) — admin-drivers-libraries.spec.ts installs
// it itself, through the web console's own "Add" button (phase 120), rather than a config file
// seeding it by shelling out to the CLI before the API starts: now that installing is a real, tested
// feature, a test exercising it is a better fixture than a shortcut around it.
export const KNOWN_DRIVER_ID = 'mysql.generic'
// The real NuGet package id, not the "mysql-connector" catalog shorthand the Add button's own request
// names — a library's id is always its package id, see
// architecture/planning/todo/follow-up-library-install-paths-disagree-on-the-resulting-library-id.md.
export const KNOWN_DRIVER_LIBRARY = 'MySqlConnector'
