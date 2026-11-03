// Hand-written input validation: every field has a type, an explicit size cap and, where useful, a pattern.
// Unknown members are rejected. Any failure is a 400 `invalid_request` with no detail.

import { HttpError } from "../http.ts";

function invalid(): never {
  throw new HttpError(400, "invalid_request");
}

/** A plain object whose members are all in `allowed` and include every name in `required`. */
export function object(value: unknown, allowed: readonly string[], required: readonly string[] = allowed): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) invalid();
  const record = value as Record<string, unknown>;
  for (const key of Object.keys(record)) if (!allowed.includes(key)) invalid();
  for (const key of required) if (!(key in record)) invalid();
  return record;
}

export interface StringRule {
  min?: number;
  max: number;
  pattern?: RegExp;
}

export function string(value: unknown, rule: StringRule): string {
  if (typeof value !== "string") invalid();
  if (value.length < (rule.min ?? 1) || value.length > rule.max) invalid();
  if (rule.pattern && !rule.pattern.test(value)) invalid();
  return value;
}

export function optionalString(value: unknown, rule: StringRule): string | undefined {
  return value === undefined ? undefined : string(value, rule);
}

export function integer(value: unknown, min: number, max: number): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value < min || value > max) invalid();
  return value;
}

export function optionalInteger(value: unknown, min: number, max: number): number | undefined {
  return value === undefined ? undefined : integer(value, min, max);
}

export function boolean(value: unknown): boolean {
  if (typeof value !== "boolean") invalid();
  return value;
}

export function optionalBoolean(value: unknown): boolean | undefined {
  return value === undefined ? undefined : boolean(value);
}

export function oneOf<T extends string>(value: unknown, options: readonly T[]): T {
  if (typeof value !== "string" || !(options as readonly string[]).includes(value)) invalid();
  return value as T;
}

/** A path parameter: a lower-case UUID as issued by the service. */
export function uuid(value: string | undefined): string {
  if (value === undefined || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)) invalid();
  return value;
}

/** Removes control and format characters from display text and trims it to `max` characters. */
export function cleanDisplayText(value: string, max: number): string {
  return value.replace(/[\p{Cc}\p{Cf}]/gu, "").trim().slice(0, max);
}
