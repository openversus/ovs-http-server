
import { logger } from "../config/logger";
import * as dgram from "dgram";
// creating a client socket
var client = dgram.createSocket('udp4');

//buffer msg
var data = Buffer.from('siddheshrane');

client.on('message', function (msg, info) {
  logger.info('Data received from server : ' + msg.toString());
  logger.info('Received %d bytes from %s:%d\n', msg.length, info.address, info.port);
});

//sending msg
client.send(data, 41234, '127.0.0.1', function (error) {
  if (error) {
    client.close();
  } else {
    logger.info('Data sent !!!');
  }
});
