import { randomInt } from "crypto";
import { logger } from "../config/logger";
import { PlayerTesterModel } from "../database/PlayerTester";

export class NameGenerator {
  lowerBounds: number;
  upperBounds: number;
  prefix: string;
  suffix: number;
  name: string;

  constructor() {
    this.lowerBounds = 1000000000000;
    this.upperBounds = 9999999999999;
    this.prefix = "OpenVersus_";
    this.suffix = 0;
    this.name = "";
  }

  Generate(): string;
  Generate(prefix: string): string;
  Generate(lowerBounds: number, upperBounds: number): string;
  Generate(lowerBounds: number, upperBounds: number, prefix: string): string;
  Generate(): string {
    switch (arguments.length) {
      case 0:
        return this._generate();
      case 1:
        this.prefix = arguments[0];
        break;
      case 2:
        this.lowerBounds = arguments[0];
        this.upperBounds = arguments[1];
        break;
      case 3:
        this.lowerBounds = arguments[0];
        this.upperBounds = arguments[1];
        this.prefix = arguments[2];
        break;
    }
    return this._generate();
  }
  _generate(): string {
    this.suffix = randomInt(this.lowerBounds, this.upperBounds);
    this.name = `${this.prefix}${this.suffix}`;
    return this.name;
  }

  static NewName(): string {
    const generator = new NameGenerator();
    return generator.Generate(generator.prefix);
  }

  static IsUnique(name: string): boolean {
    const existingName = PlayerTesterModel.findOne({ name });
    return !existingName;
  }

  static async IsUniqueAsync(name: string): Promise<boolean> {
    const existingName = await PlayerTesterModel.findOne({ name });
    return !existingName;
  }
}

// import { randomInt } from "crypto";
// import { logger } from "../config/logger";
// import { PlayerTesterModel } from "../database/PlayerTester";

// export class NameGenerator {

//   lowerBounds: number;
//   upperBounds: number;
//   prefix: string;
//   suffix: number;
//   name: string;

//   constructor() {
//     this.lowerBounds = 1000000000000;
//     this.upperBounds = 9999999999999;
//     this.prefix = "OpenVersus_";
//     this.suffix = 0;
//     this.name = "";
//   }

//   Generate(): string
//   Generate(prefix: string): string
//   Generate(lowerBounds: number, upperBounds: number): string
//   Generate(lowerBounds: number, upperBounds: number, prefix: string): string
//   Generate(): string {
//     switch (arguments.length) {
//       case 0:
//         return this._generate();
//       case 1:
//         this.prefix = arguments[0];
//         break;
//       case 2:
//         this.lowerBounds = arguments[0];
//         this.upperBounds = arguments[1];
//         break;
//       case 3:
//         this.lowerBounds = arguments[0];
//         this.upperBounds = arguments[1];
//         this.prefix = arguments[2];
//         break;
//     }
//     return this._generate();
//   }
//   _generate(): string {
//     this.suffix = randomInt(this.lowerBounds, this.upperBounds);
//     this.name = `${this.prefix}${this.suffix}`;
//     return this.name;
//   }

//   static NewName(): string {
//     const generator = new NameGenerator();
//     return generator.Generate("Blah_");
//   }

//   static IsUnique(name: string): boolean {
//     const existingName = PlayerTesterModel.findOne({ name });
//     return !existingName;
//   }

//   static async IsUniqueAsync(name: string): Promise<boolean> {
//     const existingName = await PlayerTesterModel.findOne({ name });
//     return !existingName;
//   }
// }
