# OpenVersus

This project is a fork and based on the fantastic work of [multiversuskoth](https://github.com/multiversuskoth) and [thethiny](https://github.com/thethiny). The original repository is located [here](https://github.com/multiversuskoth/mvs-http-server).


This is a very early proof-of-concept deployment, and rock-solid stability is not guaranteed, nor should it be expected. A more comprehensive `README` will be added in a later commit, but for now, here are the important bits:

1. The `build.sh` script in the root of this repo will build a docker image containing Mongo, Redis, and the OpenVersus HTTP servers
2. The UDP rollback server [is available here](https://github.com/openversus/ovs-udp-server) in its own repository, along with its own Dockerfile and example `Compose` stack file. A prebuilt, statically-linked binary of this server is also included in the container image and can be configured for use via environment variables (see `.env.example` in the `examples` directory), but running it, or multiple copies of it, in its/their own container is the expected default.
3. Example files that are expected to be available for use by `docker compose` or mapped in to the OpenVersus docker container are available in the `examples` directory in the root of this repo.

I will write a more comprehensive `README` and add a more production-ready example in a later commit, but this should be enough to stand up an instance in the meantime.

What follows is relevant text from the DMCA concerning reverse engineering and works based upon it, along with the `README` document from the [original repo](https://github.com/multiversuskoth).


`17 U.S. Code § 1201 (f)`

(f) Reverse Engineering—

1. Notwithstanding the provisions of subsection (a)(1)(A), a person who has lawfully obtained the right to use a copy of a computer program may circumvent a technological measure that effectively controls access to a particular portion of that program for the sole purpose of identifying and analyzing those elements of the program that are necessary to achieve interoperability of an independently created computer program with other programs, and that have not previously been readily available to the person engaging in the circumvention, to the extent any such acts of identification and analysis do not constitute infringement under this title.
   
2. Notwithstanding the provisions of subsections (a)(2) and (b), a person may develop and employ technological means to circumvent a technological measure, or to circumvent protection afforded by a technological measure, in order to enable the identification and analysis under paragraph (1), or for the purpose of enabling interoperability of an independently created computer program with other programs, if such means are necessary to achieve such interoperability, to the extent that doing so does not constitute infringement under this title.

3. The information acquired through the acts permitted under paragraph (1), and the means permitted under paragraph (2), may be made available to others if the person referred to in paragraph (1) or (2), as the case may be, provides such information or means solely for the purpose of enabling interoperability of an independently created computer program with other programs, and to the extent that doing so does not constitute infringement under this title or violate applicable law other than this section.


4. For purposes of this subsection, the term “interoperability” means the ability of computer programs to exchange information, and of such programs mutually to use the information which has been exchanged.

---
#### Original `README` is as follows:
---

# MVS HTTP & Websocket Server

Express server with a mongoDB backend

Everything is typescript including using typegoose.

## Routes

Routes are auto generated all routes from a postmancollection.json from [mvs-dump](https://github.com/multiversuskoth/mvs-dump). `router.ts` should be edited since its auto generated

## Request/Response/Query Interfaces

Request/Response/Query interfaces are also auto generated and inside the interfaces folder. These are auto added inside the router.ts file and should also be used in the handlers req, res interfaces to get body and response auto completion

## Handlers

All handlers are inside the `handlers` folder. To implement a handler copy the handler name and signature from the router.ts and create a new file inside the handlers folder. Make sure to add your file to the handlers/index.ts file so it can be picked up by the auto generated router.ts file

## Hydra middleware

The hydra middleware decodes/encodes x-ag-binary responses and adds them to the req body. Response will be encoded back to hydra.

Common hydra headers are also appended to all requests

## Schema

Databse schema are contained inside the database folder and use typegoose models
