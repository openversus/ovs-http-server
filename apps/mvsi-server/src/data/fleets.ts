import type { Region } from "../modules/matchmaking/matchmaking.matching";

export type FleetServer = {
  locationid: number;
  regionid: string;
  ipv4: string;
  ipv6: string;
  port: number;
  region: Region;
};

export const FLEET_SERVERS: FleetServer[] = [
  {
    locationid: 0,
    regionid: "19c465a7-f21f-11ea-a5e3-0954f48c5682",
    ipv4: "108.61.219.200",
    ipv6: "",
    port: 9000,
    region: "WEST_US",
  },
  {
    locationid: 1,
    regionid: "19bf18ce-f21f-11ea-b94f-f946c68d5a4f",
    ipv4: "107.191.51.12",
    ipv6: "",
    port: 9000,
    region: "CENTRAL_US",
  },
  {
    locationid: 2,
    regionid: "19c714ff-f21f-11ea-b144-4d87911ee195",
    ipv4: "108.61.149.182",
    ipv6: "",
    port: 9000,
    region: "EAST_US",
  },
  {
    locationid: 3,
    regionid: "657d35f8-ca5e-11ec-85a7-b6a275757dc0",
    ipv4: "216.238.66.16",
    ipv6: "",
    port: 9000,
    region: "MEXICO_CITY",
  },
  {
    locationid: 5,
    regionid: "0d77609a-c3c5-11eb-8890-0242ac110002",
    ipv4: "216.238.98.118",
    ipv6: "",
    port: 9000,
    region: "SAO_PAULO",
  },
  {
    locationid: 6,
    regionid: "19c32880-f21f-11ea-a907-512b3194e649",
    ipv4: "64.176.178.136",
    ipv6: "",
    port: 9000,
    region: "MANCHESTER",
  },
  {
    locationid: 7,
    regionid: "19cf42e3-f21f-11ea-a4fe-05a850423fbf",
    ipv4: "108.61.212.117",
    ipv6: "",
    port: 9000,
    region: "SYDNEY",
  },
  {
    locationid: 8,
    regionid: "19c9c88a-f21f-11ea-bbf4-cb9c4fdeb10a",
    ipv4: "108.61.201.151",
    ipv6: "",
    port: 9000,
    region: "TOKYO",
  },
];

/** Look up a fleet server by its region UUID. */
export function getFleetByRegionId(regionId: string): FleetServer | undefined {
  return FLEET_SERVERS.find((s) => s.regionid === regionId);
}
