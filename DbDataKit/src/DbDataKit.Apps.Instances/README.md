# DbDataKit.Apps.Instances

Instances of an app (plan 26 section 6.1, AP04). An app is published as the definition `app/<name>` (its manifest is the content) and an instance as `app-instance/<name>`, both in the group's state through `DefinitionStore` (plan 20, O03), so they are versioned, served by channel, audited and rolled back like any published definition, and every serving instance of a group sees the same.

```csharp
var store = new AppInstanceStore(new DefinitionStore(state));
store.PublishApp(manifestYaml, "ada");                       // app/sales-planning, version 1, 2, ...
store.Save(new AppInstance("acme", "sales-planning", Pin: 1,
    Settings: new Dictionary<string, string> { ["theme"] = "blue" },
    Bindings: new Dictionary<string, string> { ["territories"] = "acme" }), "ada");
store.Save(new AppInstance("globex", "sales-planning"), "ada"); // runs whatever the channel serves
var running = store.Resolve("acme");                           // the instance, the app version it runs, its manifest
```

An app version is an `AppPackage`: the manifest and the files the host serves. `Pin` is the app version an instance runs, or none for the channel's current: promoting version 2 touches only the unpinned instances. A binding must name a model the app's manifest uses. Tokens are scoped to one instance: `AppInstanceAudience.Of(name)` is the audience a token for it names, and a validator built for another instance refuses it.
