import { Request, Response } from "express";
import { logger } from "../config/logger";
import * as path from "path";

const serviceName = "Handlers.Images";
const logPrefix: string = `[${serviceName}]:`;

export async function handleImages(req: Request<{ image: string }, {}, {}, {}>, res: Response) {

  if (!req || !req.params || !req.params.image) {
    logger.error(`${logPrefix} No image specified in request`);
    return res.status(400).send("Image parameter is required");
  }

  const nc_name_background = "name_change_background.jpeg";
  const nc_name_logo = "OpenVersus_logo.png";
  const nc_name_gif1 = "200w.webp";
  const nc_name_gif2 = "200.webp";
  const nc_name_gif3 = "200w2.webp";
  const nc_name_gifBB = "multiversus-bugs-bunny.gif";

  const nc_path_background = path.join(__dirname, `../static/${nc_name_background}`);
  const nc_path_logo = path.join(__dirname, `../static/${nc_name_logo}`);
  const nc_path_gif1 = path.join(__dirname, `../static/${nc_name_gif1}`);
  const nc_path_gif2 = path.join(__dirname, `../static/${nc_name_gif2}`);
  const nc_path_gif3 = path.join(__dirname, `../static/${nc_name_gif3}`);
  const nc_path_gifBB = path.join(__dirname, `../static/${nc_name_gifBB}`);

  logger.info(`${logPrefix} Received request for image: ${req.params.image}`);

  switch (req.params.image) {
    case nc_name_background:
      trySendFile(nc_path_background, res);
      return;
    case nc_name_logo:
      trySendFile(nc_path_logo, res);
      return;
    case nc_name_gif1:
      trySendFile(nc_path_gif1, res);
      return;
    case nc_name_gif2:
      trySendFile(nc_path_gif2, res);
      return;
    case nc_name_gif3:
      trySendFile(nc_path_gif3, res);
      return;
    case nc_name_gifBB:
      trySendFile(nc_path_gifBB, res);
      return;
    default:
      res.status(404).send("Image not found");
      return;
  }

  // If we somehow got here without sending a file, Node is drunk, so just return 404
  logger.error(`${logPrefix} Reached end of handler without sending a file, this should never happen. Request params: ${JSON.stringify(req.params)}`);
  res.status(404).send("Image not found");
  return;
}

async function trySendFile(stringPath: string, res: Response) {
  try {
    res.status(200).sendFile(stringPath);
    return true;
  }
  catch (error) {
    logger.error(`${logPrefix} Error sending file ${stringPath}: ${error}`);
    res.status(500).send("Error sending image");
    return false;
  }
}
