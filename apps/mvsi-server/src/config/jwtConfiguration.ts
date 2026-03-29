import type { Algorithm } from "jsonwebtoken";

const jwtConfiguration: {
  algorithm: Algorithm;
  expiresSeconds: number;
} = {
  algorithm: "RS256",
  expiresSeconds: 24 * 60 * 60,
};

export default jwtConfiguration;
