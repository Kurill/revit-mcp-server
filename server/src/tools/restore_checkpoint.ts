import { errorMessage } from "../utils/errorUtils.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

export function registerRestoreCheckpointTool(server: McpServer) {
  server.tool(
    "restore_checkpoint",
    "Open a checkpoint of the active model as <model>_restored_<time>.rvt next to the model and make it the active document (workshared models are opened detached from central). The model that was being worked on stays open and unchanged: tell the user to close it without saving to keep the restored copy. Use list_checkpoints for the names.",
    {
      checkpoint: z
        .string()
        .describe("Checkpoint file name from list_checkpoints, e.g. '20261009-153012_before_dimensions.rvt'."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("restore_checkpoint", args);
        }, 310000);
        return rawToolResponse("restore_checkpoint", response);
      } catch (error) {
        return rawToolError("restore_checkpoint", `Restore checkpoint failed: ${errorMessage(error)}`);
      }
    }
  );
}
