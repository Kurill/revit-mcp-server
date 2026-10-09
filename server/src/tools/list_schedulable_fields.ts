import { errorMessage } from "../utils/errorUtils.js";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

export function registerListSchedulableFieldsTool(server: McpServer) {
  server.tool(
    "list_schedulable_fields",
    "List all available schedule fields for a given Revit category. Use before creating schedules to discover valid field names.",
    {
      categoryName: z
        .string()
        .describe(
          "Category: BuiltInCategory name (e.g. 'OST_Rooms', 'OST_Doors'), English name ('Doors') or localized display name"
        ),
      scheduleType: z
        .enum(["regular", "material_takeoff", "key_schedule"])
        .optional()
        .default("regular")
        .describe("Schedule type to query fields for. Default: regular"),
      nameFilter: z
        .string()
        .optional()
        .describe("Case-insensitive substring to filter field names, e.g. 'Fire' or 'Width'."),
      limit: z
        .number()
        .int()
        .positive()
        .optional()
        .default(200)
        .describe("Maximum number of fields to return (default 200). Response includes totalCount and truncated."),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("list_schedulable_fields", {
            categoryName: args.categoryName,
            scheduleType: args.scheduleType ?? "regular",
            nameFilter: args.nameFilter,
            limit: args.limit ?? 200,
          });
        });
        return rawToolResponse("list_schedulable_fields", response);
      } catch (error) {
        return rawToolError(
          "list_schedulable_fields",
          `List schedulable fields failed: ${errorMessage(error)}`
        );
      }
    }
  );
}
