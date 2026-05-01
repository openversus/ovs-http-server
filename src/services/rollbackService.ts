import { logger, logwrapper } from "../config/logger";
import { readFileSync, appendFileSync } from "fs";
import { join } from "path";
import { randomInt, createHmac } from "crypto";
import * as http from "http";
import { redisGetCurrentRollbackPort, redisSetCurrentRollbackPort } from "../config/redis";
import env from "../env/env";

const serviceName = "Services.RollbackService";
const logPrefix: string = `[${serviceName}]:`;

const webhookHost: string = env.WEBHOOK_HOST ?? "localhost";
const webhookPort: number = env.WEBHOOK_PORT || 9001;
const webhookDeployPath: string = env.WEBHOOK_DEPLOY_PATH ?? "/hooks/deploy-rollback-server";
const webhookDestroyPath: string = env.WEBHOOK_DESTROY_PATH ?? "/hooks/destroy-rollback-server";
const deployKey: string = env.WEBHOOK_HMAC_SECRET ?? "CHANGEME";
const deployInfoFile: string = env.DEPLOY_ROLLBACK_DEFAULTS_FILE ?? "../data/deploy-rollback-defaults.json";
export const useOnDemandRollback: boolean = env.ON_DEMAND_ROLLBACK === 1;
const onDemandRollbackPortLow: number = env.ON_DEMAND_ROLLBACK_PORT_LOW || 60000;
const onDemandRollbackPortHigh: number = env.ON_DEMAND_ROLLBACK_PORT_HIGH || 64000;

export interface IDeployInfo
{
  entrypoint: string;
  image: string;
  ovs_server: string;
  port: number;
  servicename: string;
  created_at: number;
}

export class DeployInfo implements IDeployInfo {
  entrypoint: string;
  image: string;
  ovs_server: string;
  port: number;
  servicename: string;
  created_at: number;

  constructor();
  constructor(entrypoint: string, image: string, ovs_server: string, port: number, servicename: string);
  constructor() {
    let rollbackPort = DeployInfo.getRandomRollbackPort();
    let createdAt = Date.now();

    switch (arguments.length) {
      case 0:
        this.entrypoint = "";
        this.image = "";
        this.ovs_server = "";
        this.port = rollbackPort;
        this.servicename = "";
        this.created_at = createdAt;
        return;
      case 1:
        this.entrypoint = arguments[0];
        this.image = "";
        this.ovs_server = "";
        this.port = rollbackPort;
        this.servicename = "";
        this.created_at = createdAt;
        return;
      case 2:
        this.entrypoint = arguments[0];
        this.image = arguments[1];
        this.ovs_server = "";
        this.port = rollbackPort;
        this.servicename = "";
        this.created_at = createdAt;
        return;
      case 3:
        this.entrypoint = arguments[0];
        this.image = arguments[1];
        this.ovs_server = arguments[2];
        this.port = rollbackPort;
        this.servicename = "";
        this.created_at = createdAt;
        return;
      case 4:
        this.entrypoint = arguments[0];
        this.image = arguments[1];
        this.ovs_server = arguments[2];
        this.port = arguments[3];
        this.servicename = "";
        this.created_at = createdAt;
        return;
      case 5:
        this.entrypoint = arguments[0];
        this.image = arguments[1];
        this.ovs_server = arguments[2];
        this.port = arguments[3];
        this.servicename = arguments[4];
        this.created_at = createdAt;
        return;
    }

    this.entrypoint = "";
    this.image = "";
    this.ovs_server = "";
    this.port = DeployInfo.getRandomRollbackPort();
    this.servicename = "";
    this.created_at = createdAt;
    return;
  }

  static Deploy(deployObject: IDeployInfo): boolean {
    let deployer = new DeployInfo(
      deployObject.entrypoint,
      deployObject.image,
      deployObject.ovs_server,
      deployObject.port,
      deployObject.servicename
    );
    return deployer._deploy(deployObject);
  }

  _deploy(): boolean;
  _deploy(deployObject: IDeployInfo): boolean;
  _deploy(): boolean
  {
    let finalDeployObject: IDeployInfo = this;
    if (arguments.length === 1) {
      finalDeployObject = arguments[0];
    }

    let rawStringPayload = JSON.stringify(finalDeployObject);
    let payloadHmacSha = createHmac("sha1", deployKey)
                           .update(rawStringPayload)
                           .digest("hex");
    
    let options = {
      host: webhookHost,
      port: webhookPort,
      path: webhookDeployPath,
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Deploy-Key': payloadHmacSha
      },
    };

  logwrapper.verbose(`${logPrefix} Deploying rollback server with the following info: ${JSON.stringify(finalDeployObject)}`);
    try {
      const deployRequest = http.request(options, (res) => {
      let responseData = '';

      res.on('data', (chunk) => {
        responseData += chunk;
      });

      res.on('end', () => {
        logwrapper.info(`${logPrefix} Response from webhook server: ${responseData}`);
      });
    });

    deployRequest.on('error', (error) => {
      logwrapper.error(`${logPrefix} Error making deployment request to webhook server: ${JSON.stringify(error)}`);
    });

    deployRequest.write(rawStringPayload);
    deployRequest.end();

    logwrapper.info(`${logPrefix} Successfully sent deployment request to webhook server for rollback server with the following info: ${JSON.stringify(finalDeployObject)}`);
    
    return true;
    }
    catch (error) {
      logwrapper.error(`${logPrefix} Failed to send deployment request to webhook server for rollback server with the following info: ${JSON.stringify(finalDeployObject)}. Error: ${JSON.stringify(error)}`);
      return false;
    }
  }

  // The "Destroy" endpoints are unused for now, as the deploy script contains an embedded
  // shell script that self-destructs the rollback server instance after 9 minutes. These are just here
  // b uj9=0mkfor future use by the rollback server itself to call after its End of Match call, and the
  // auto-selflo+-destruct timer will just be there as a redundant deprovisioning measure.
  static Destroy(destroyObject: IDeployInfo): boolean {
    let destroyer = new DeployInfo(
      destroyObject.entrypoint,
      destroyObject.image,
      destroyObject.ovs_server,
      destroyObject.port,
      destroyObject.servicename
    );
    return destroyer._destroy(destroyObject);
  }

  _destroy(): boolean;
  _destroy(destroyObject: IDeployInfo): boolean;
  _destroy(): boolean
  {
    let finalDestroyObject: IDeployInfo = this;
    if (arguments.length === 1) {
      finalDestroyObject = arguments[0];
    }

    const destroyPayload = {
      servicename: finalDestroyObject.servicename,
      port: finalDestroyObject.port,
      directory: `${finalDestroyObject.servicename}-${finalDestroyObject.port}`,
    }

    const rawStringPayload = JSON.stringify(destroyPayload);
    let payloadHmacSha = createHmac("sha1", deployKey)
                            .update(rawStringPayload)
                            .digest("hex");

    let options = {
      host: webhookHost,
      port: webhookPort,
      path: webhookDestroyPath,
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Destroy-Key': payloadHmacSha
      },
    };

    try {
      const destroyRequest = http.request(options, (res) => {
        let responseData = '';

        res.on('data', (chunk) => {
          responseData += chunk;
        });

        res.on('end', () => {
          logwrapper.info(`${logPrefix} Response from webhook server: ${responseData}`);
        });
      });

      destroyRequest.on('error', (error) => {
        logwrapper.error(`${logPrefix} Error making destroy request to webhook server: ${JSON.stringify(error)}`);
      });

      destroyRequest.write(rawStringPayload);
      destroyRequest.end();

      logwrapper.info(`${logPrefix} Successfully sent destroy request to webhook server for rollback server with the following info: ${JSON.stringify(finalDestroyObject)}`);
      return true;
    }
    catch (error) {
      logwrapper.error(`${logPrefix} Failed to send destroy request to webhook server for rollback server with the following info: ${JSON.stringify(finalDestroyObject)}. Error: ${JSON.stringify(error)}`);
      return false;
    }
  }

  static getRandomRollbackPort(): number {
    if (useOnDemandRollback) {
      return onDemandRollbackPortLow;
    }
    return randomInt(env.ROLLBACK_UDP_PORT_LOW, env.ROLLBACK_UDP_PORT_HIGH);
  }

  static GetEmpty(): IDeployInfo {
    var returnObject = new DeployInfo() as IDeployInfo;
    returnObject.port = DeployInfo.getRandomRollbackPort();
    return returnObject;
  }

  static async getCurrentRollbackPort(): Promise<number> {
    let currentPort: number = onDemandRollbackPortLow;

    try {
      currentPort = await redisGetCurrentRollbackPort() || onDemandRollbackPortLow;
      logwrapper.verbose(`${logPrefix} Current rollback port from Redis: ${currentPort}`);
    } catch (error) {
      logwrapper.error(`${logPrefix} Error getting current rollback port from Redis. Error: ${JSON.stringify(error)}`);
      currentPort = DeployInfo.getRandomRollbackPort();
      logwrapper.verbose(`${logPrefix} Returning random rollback port: ${currentPort}`);
    }
    return currentPort;
  }

  static async getNextRollbackPort(increment?: boolean): Promise<number> {
    if (!increment) {
      return (await redisGetCurrentRollbackPort() || onDemandRollbackPortLow) + 1;
    }
    try {
      let nextPort = (await DeployInfo.getCurrentRollbackPort()) + 1;
      if (nextPort > env.ROLLBACK_UDP_PORT_HIGH) {
        nextPort = env.ROLLBACK_UDP_PORT_LOW;
      }
      await redisSetCurrentRollbackPort(nextPort);
      logwrapper.verbose(`${logPrefix} Incremented current rollback port in Redis to: ${nextPort}`);
      return nextPort;
    } catch (error) {
      logwrapper.error(`${logPrefix} Error incrementing current rollback port in Redis. Error: ${JSON.stringify(error)}`);
      return (await redisGetCurrentRollbackPort() || onDemandRollbackPortLow) + 1;
    }
  }

  static async setCurrentRollbackPort(deployObject?: IDeployInfo, port?: number): Promise<IDeployInfo> {
    let returnObject: IDeployInfo = deployObject || new DeployInfo() as IDeployInfo;
    returnObject.port = port || (await DeployInfo.getCurrentRollbackPort()) + 1;

    if (returnObject.port > env.ROLLBACK_UDP_PORT_HIGH) {
      returnObject.port = env.ROLLBACK_UDP_PORT_LOW;
    }

    try {
      await redisSetCurrentRollbackPort(returnObject.port);
      logwrapper.verbose(`${logPrefix} Set current rollback port in Redis: ${returnObject.port}`);
    } catch (error) {
      logwrapper.error(`${logPrefix} Error setting current rollback port in Redis. Error: ${JSON.stringify(error)}`);
    }
    return returnObject;
  }

  static GetDefaults(): IDeployInfo {
    logwrapper.verbose(`${logPrefix} Reading deploy info from: ${deployInfoFile}`);
    let returnObject: IDeployInfo = new DeployInfo() as IDeployInfo;
    try {
      var data = readFileSync(join(__dirname, deployInfoFile), "utf-8");
      returnObject = JSON.parse(data) as IDeployInfo;
      returnObject.port = DeployInfo.getRandomRollbackPort();
    }
    catch (error) {
      logwrapper.error(`${logPrefix} Error reading deploy info from: ${deployInfoFile}, returning empty object. Error: ${JSON.stringify(error)}`);
    }

  return returnObject;
  }
}

export function getDefaultDeployInfo(): IDeployInfo {
  let returnObject = new DeployInfo() as IDeployInfo;

  try {
    returnObject = DeployInfo.GetDefaults() as IDeployInfo;
  }
  catch (error) {
    logger.error(`${logPrefix} Error reading deploy info from: ${deployInfoFile}. Error: ${JSON.stringify(error)}`);
  }

  return returnObject;
}
