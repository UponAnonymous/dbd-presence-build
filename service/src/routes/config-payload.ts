// The payload of the signed config document (FR-ONL-08, PLAN_V2 §6.4).
// The capture and detection switches can only *disable* (SAFETY_AND_PRIVACY §2.3): `disable` holds booleans meaning
// "force off", and there is no member that can enable or opt in to anything.

import type { AppList, DiscordApp } from "../db/apps.ts";
import type { AccessMode, Settings } from "../db/settings.ts";

/**
 * The built-in application (src/DbdPresence.Configuration/ApplicationCatalog.cs). Migration 0002 seeds the owner's
 * list with it; it is used on its own only if the stored list were ever empty.
 */
export const APPLICATIONS = [
  {
    key: "dbd",
    applicationId: "1563291325107937340",
    name: "Dead by Daylight",
    description: "Unofficial fan-made presence — not affiliated with Behaviour Interactive",
  },
] as const satisfies readonly DiscordApp[];

export const BUILT_IN_APPS: AppList = { applications: APPLICATIONS.map((a) => ({ ...a })), defaultApplication: APPLICATIONS[0].key };

export interface ConfigPayload {
  schemaVersion: 1;
  accessMode: AccessMode;
  applications: DiscordApp[];
  defaultApplication: string;
  disable: { capture: boolean; detection: boolean };
  emergencyStop: boolean;
  minimumVersion: string;
  banner: string | null;
}

export function configPayload(settings: Settings, apps: AppList = BUILT_IN_APPS): ConfigPayload {
  return {
    schemaVersion: 1,
    accessMode: settings.accessMode,
    applications: apps.applications.map((a) => ({ key: a.key, applicationId: a.applicationId, name: a.name, description: a.description })),
    defaultApplication: apps.defaultApplication,
    disable: { capture: settings.disableCapture, detection: settings.disableDetection },
    emergencyStop: settings.emergencyStop,
    minimumVersion: settings.minimumVersion,
    banner: settings.banner,
  };
}
