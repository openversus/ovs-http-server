import { schema, types } from "papr";
import papr, { dbEmitter } from "../papr";

const matchesSchema = schema(
	{
		created_at: types.date({ required: true }),
		updated_at: types.date({ required: true }),
		completion_time: types.date({ required: true }),
		win: types.array(types.string(), { required: true }),
		loss: types.array(types.string(), { required: true }),
		draw: types.boolean({ required: true }),
		server_data: types.any(),
		players: types.any(),
		cluster: types.string({ required: true }),
		template: types.any(),
		access: types.string({ required: true }),
	},
);

dbEmitter.once("connected", async () => {
	await MatchesModel.collection.createIndex(
		{ completion_time: 1 },
		{ expireAfterSeconds: 2592000 },
	);
	await MatchesModel.collection.createIndex({ "players.all.account_id": 1 });
});

export type MatchesDoc = (typeof matchesSchema)[0];
export const MatchesModel = papr.model("matches", matchesSchema);
