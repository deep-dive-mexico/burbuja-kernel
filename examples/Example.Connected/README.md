# Independent connected application

This example is an HTTP application using only `Burbuja.Core.Abstractions` and
`Burbuja.Core.Sdk`. It starts no Core/Agents/Compute runtime and opens no kernel
database. It demonstrates the [connected storage candidate](../../docs/connected-applications.md).

Set `Application:Environment=development`, `Application:Tenant`,
`Application:Name`, `Kernel:Url` and its registered `Kernel:Credential` through
private environment configuration. The kernel registration grants
`WriteNamespaces=[Example.Connected]` and `EventPrefixes=[example.]`.
Use `ASPNETCORE_URLS` to choose an independent loopback listener.

`GET /binding` shows negotiated identity and loaded contract/SDK assemblies.
`PUT /notes/{name}` and `GET /notes/{name}` use the application binding through
typed network persistence calls. Kernel loss returns **503 KernelUnavailable**;
there is no local database fallback.

For a complete isolated two-application demonstration, run
`python3 scripts/prove-connected-apps.py` from the kernel root. It generates
temporary credentials, publishes the kernel/plugins and this application,
starts three processes, tests isolation/restarts/unavailability, writes an
evidence receipt and stops only its own processes. It makes no model calls.
