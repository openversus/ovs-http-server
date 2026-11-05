# OpenVersus

This project has been completely rewritten from the ground-up in C#, but it started its life as a fork, and was based on the fantastic work of [multiversuskoth](https://github.com/multiversuskoth) and [thethiny](https://github.com/thethiny). The original (TypeScript-based) repository is located [here](https://github.com/multiversuskoth/mvs-http-server).


**OpenVersus** is a replacement server-side infrastructure backend for MultiVersus: HTTP, websocket, matchmaking,
match-flow, rollback netcode, and related services which the game client talks to, written in C# (.NET 10) as one solution
of independent services, plus the admin tool `ovsctl`. The original TypeScript fork it grew out of is kept as a reference
under `legacy/reference/ts_server/`; it is kept strictly for reference, and is no longer run, maintained, or supported.
The only relevant, live code in this repo belongs to the from-scratch C# reimplementation.

Where things are:

1. `src/` holds the services (one project each: http, access, social, lobbies, web, realtime, edge, matchmaking,
   matchflow, arenas, steam) and the shared `OpenVersus.Server.Core`; `tests/` their test projects; `test.sh` runs
   them against local throwaway stores.
2. `Dockerfile` builds every service's image from one build of the solution (`build-images.sh` builds them all).
3. `deploy/` is the example production layout: Docker Compose projects for the stores and the services, the HAProxy router,
   example env files, and `build.sh` / `ovs.sh` to pack a release and run it on a host. Start with `deploy/README.md`.
4. `docs/` describes the protocol work (routes, realtime, SSC, Arenas, Rifts, seasons, bans); `tools/` holds the
   generators and diff harnesses used during reimplementation.
5. The UDP rollback netcode relay and the players' P2P nodes live in their own repository
   ([ovs-rollback-server](https://github.com/openversus/ovs-rollback-server)); `deploy/build.sh` builds its image alongside
   the rest of the images native to this repo.

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



---

OpenVersus is not affiliated with, nor endorsed by, WB, PFG, its developers, or any other related entity.
OpenVersus is not affiliated with, nor endorsed by, any other modding project or effort, the developers of those projects, or any other related entity.
This software is provided on an as-is, sans-warranty basis as detailed in the `LICENSE` file. Use at your own risk and discretion.

