/**
 * Pure formatting of an MCP tools/list result into tool-schemas.txt.
 * Shared by generate-tool-schemas.mjs (writes it at build time) and the vitest
 * drift test (compares it against the committed copy).
 */

/** tool-schemas.txt: one compact signature line per tool, sorted by name. */
export function formatToolSchemasTxt(tools) {
  const lines = [...tools]
    .sort((a, b) => a.name.localeCompare(b.name))
    .map((t) => {
      const props = t.inputSchema?.properties || {};
      const required = new Set(t.inputSchema?.required || []);

      const params = Object.entries(props)
        .map(([k, v]) => {
          let sig;
          if (v.type === "object" && v.properties) {
            const sub = Object.entries(v.properties)
              .map(([pk, pv]) => `${pk}:${pv.type || "?"}`)
              .join(",");
            sig = `${k}:{${sub}}`;
          } else if (v.enum) {
            sig = `${k}:${v.enum.join("|")}`;
          } else {
            sig = `${k}:${v.type || "?"}`;
          }
          if (required.has(k)) sig += "!";
          return sig;
        })
        .join(", ");

      return `${t.name}(${params})`;
    });
  return lines.join("\n") + "\n";
}
