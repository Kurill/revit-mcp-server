# mcp-server-for-revit

MCP server component of [revit-mcp-server](https://github.com/Kurill/revit-mcp-server). It exposes Revit operations as MCP tools over stdio and forwards each call over TCP (JSON-RPC 2.0) to the Revit add-in, which has to be installed and running inside Revit.

The release ZIPs bundle a built copy of this server under `revit_mcp_plugin\Commands\RevitMCPCommandSet\server\`; the installer points Claude Desktop at it. See the [project README](../README.md) for setup.

## Development

```bash
npm ci
npm run build   # also regenerates tool-schemas.txt
npm test
```

CI fails when the regenerated schemas are not committed.
