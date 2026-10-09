import { errorMessage } from "../utils/errorUtils.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

export function registerListCheckpointsTool(server: McpServer) {
  server.tool(
    "list_checkpoints",
    "List the checkpoints of the active model, newest first, with time and size. Names ending in _auto were made automatically before a model-changing call.",
    {},
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("list_checkpoints", args);
        }, 60000);
        return rawToolResponse("list_checkpoints", response);
      } catch (error) {
        return rawToolError("list_checkpoints", `List checkpoints failed: ${errorMessage(error)}`);
      }
    }
  );
}
