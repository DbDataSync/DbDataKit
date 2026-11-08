# DbDataKit.Hosting.Local

The local channel: how a product's command line talks to its own running server on the same machine without the network. `server pair`, `server unpair` and the admin verbs work this way, so work on a machine never needs a console.

- Windows: a named pipe `dbdata-<name>`, with an access list that lets in SYSTEM and Administrators (`LocalChannelOptions.WindowsIdentities`). Needing those rights is the proof of ownership that pairing relies on.
- Linux and macOS: a Unix socket `channel.sock` in a folder created for the server's user alone (mode 0700, the socket 0600; `GroupAccess` widens it to the server's group, 0750 and 0660). Default folder: `/run/dbdata/<name>` for root, else `$XDG_RUNTIME_DIR/dbdata/<name>`, else `/tmp/dbdata-<user>/<name>`.
- One channel per name per machine. One request per message, at most `MaxMessageBytes` (1 MiB by default); `LocalChannelServer` takes a handler from bytes to bytes, `LocalChannelClient` sends one message.
- The operating system decides who may connect; the channel adds no password. A caller on the channel is an administrator of that server, and its changes are authored as the local user (`ManagementIdentity.LocalAuthor` in `DbDataKit.Web`).
