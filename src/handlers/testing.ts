import { logger, logwrapper } from "../config/logger";
import { DeployInfo, IDeployInfo } from "../services/rollbackService";


export async function handleDeployRollbackServer(req: any, res: any) {
  let deployInfo: IDeployInfo = new DeployInfo() as IDeployInfo;
  if (!req.body)
  {
    logger.error("Received request to deploy rollback server with no body");
    res.status(400).json({ message: "No body provided" });
    return;
  }

  deployInfo = req.body as IDeployInfo;
  logger.info("Received request to deploy rollback server with the following body: " + JSON.stringify(deployInfo));

  let deployResult = false;
  try {
    deployResult = DeployInfo.Deploy(deployInfo);
  }
  catch (error) {
    logger.error("Error deploying rollback server: " + (error as Error).message);
    res.status(500).json({ message: "Error deploying rollback server" });
    return;
  }

  res.status(200).json({ message: "Rollback server deployed successfully" });
}

export async function handleDestroyRollbackServer(req: any, res: any) {
  let destroyInfo: IDeployInfo = new DeployInfo() as IDeployInfo;
  if (!req.body)
  {
    logger.error("Received request to destroy rollback server with no body");
    res.status(400).json({ message: "No body provided" });
    return;
  }

  destroyInfo = req.body as IDeployInfo;
  logger.info("Received request to destroy rollback server with the following body: " + JSON.stringify(destroyInfo));

  let destroyResult = false;
  try {
    destroyResult = DeployInfo.Destroy(destroyInfo);
  }
  catch (error) {
    logger.error("Error destroying rollback server: " + (error as Error).message);
    res.status(500).json({ message: "Error destroying rollback server" });
    return;
  }

  res.status(200).json({ message: "Rollback server destroyed successfully" });
}
