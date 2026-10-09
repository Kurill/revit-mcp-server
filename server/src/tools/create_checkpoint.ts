import { errorMessage } from "../utils/errorUtils.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

export function registerCreateCheckpointTool(server: McpServer) {
  server.tool(
    "create_checkpoint",
    "Save the active model and keep a full copy of the file in mcp-checkpoints\\<model name>\\ next to it (the last 10 are kept). Call it before a large or risky change so the user can go back with restore_checkpoint. A checkpoint is also made automatically before the first model-changing call every 30 minutes. Only models saved as files on disk; not cloud models.",
    {
      label: z
        .string()
        .optional()
        .describe("Short note added to the checkpoint file name, e.g. 'before dimensions'."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_checkpoint", args);
        }, 310000);
        return rawToolResponse("create_checkpoint", response);
      } catch (error) {
        return rawToolError("create_checkpoint", `Create checkpoint failed: ${errorMessage(error)}`);
      }
    }
  );
}
