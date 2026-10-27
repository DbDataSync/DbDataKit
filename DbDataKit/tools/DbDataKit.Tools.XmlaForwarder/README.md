# dbdata-xmla-forwarder

Records what a client (Excel, ADOMD.NET, anything that talks to Analysis Services) and an Analysis Services server say to each other, for the catalogue of Excel's XMLA and MDX traffic (plan 14 Q13, plan 19 J20). It is a TCP pass-through: it copies the bytes both ways without changing them and writes them to files. It does not read the protocol, so login, compression and signing work as they do without it.

```
dotnet run --project DbDataKit/tools/DbDataKit.Tools.XmlaForwarder -- --target localhost:2383 --out C:\recordings\excel-1 --listen 2384
```

Then give Excel (Data > Get Data > From Analysis Services) the server name `<this machine>:2384` instead of the real one, and use it as usual. `--bind 0.0.0.0` lets a client on another machine connect; the default listens on 127.0.0.1 only. Ctrl+C (or `--max-seconds`) stops it and finishes the files.

## What it writes

| File | Holds |
| --- | --- |
| `conn-0001.client.bin`, `conn-0001.server.bin` | every byte each side sent on connection 1, exactly as it crossed |
| `conn-0001.client.txt`, `conn-0001.server.txt` | the readable text found in those streams (UTF-8 and UTF-16LE runs of 8 or more characters, with their offsets), written when the connection closes |
| `session.jsonl` | one line per event: a connection opened or closed (with any error), and every chunk with its time, direction, offset in the stream file and length, so the order of requests and responses can be rebuilt |

Excel opens several connections (metadata, queries, the cube browser); each is numbered in the order it was accepted.

## Read this before using it

- **A recording is not private.** It holds whatever was sent: the DAX or MDX Excel wrote, the model's data in the responses, and the authentication exchange. Keep recordings out of the repository until the content you want is extracted from them.
- **Whether the XML is readable is not known yet.** Analysis Services' TCP protocol may compress or frame messages, in which case the `.txt` shows little and the `.bin` files need decoding. The first recording on the VM settles it; if it is compressed, the next step is to turn the compression off on the client (the connection property `Transport Compression=None`, if the provider accepts it) or to decode the frames here.
- **HTTP (msmdpump.dll) is not handled.** A server reached over HTTP speaks SOAP over HTTP with its own authentication; that wants a different forwarder.
- A client that checks the server's name or address sees the forwarder's.

## Tests

`DbDataKit/tests/DbDataKit.Tools.XmlaForwarder.Tests` runs the forwarder in front of a fake server. `SsasRecordingTests` in `DbDataKit.Semantic.Tests` records an ADOMD.NET session against a real instance (Windows; `docs/semantic/ssas-ci.md`).
