/**
 * Twosday's weekly window: one day of the week, from a start time to an end time on that day's wall clock in a
 * given time zone (daylight saving included), whatever the server's own clock is set to (UTC in prod).
 * No imports, so tests can load it without the server's configuration.
 */
export interface TwosdayWindow {
  /** 0 Sunday .. 6 Saturday. */
  day: number;
  /** "HH:MM" or "HH:MM:SS", inclusive. */
  start: string;
  /** "HH:MM" or "HH:MM:SS", exclusive; "24:00" is the end of the day. */
  end: string;
  /** An IANA time zone, such as America/New_York. */
  timeZone: string;
}

/** Tuesdays from 3 PM to midnight, US Eastern time. */
export const DEFAULT_TWOSDAY_WINDOW: TwosdayWindow = { day: 2, start: "15:00", end: "24:00", timeZone: "America/New_York" };

const WEEKDAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

/** Seconds since midnight for "HH:MM" or "HH:MM:SS" (24:00 allowed); null when it is not a time. */
export function secondsOfDay(text: string): number | null {
  const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(text.trim());
  if (!m) return null;
  const [h, min, s] = [Number(m[1]), Number(m[2]), Number(m[3] ?? 0)];
  if (min > 59 || s > 59 || h > 24 || (h === 24 && (min > 0 || s > 0))) return null;
  return h * 3600 + min * 60 + s;
}

/**
 * Whether `now` falls inside the window. Throws on a window it cannot read (a bad time, day or time zone), so the
 * caller decides what a broken setting means.
 */
export function isInTwosdayWindow(now: Date, window: TwosdayWindow): boolean {
  const start = secondsOfDay(window.start);
  const end = secondsOfDay(window.end);
  if (start === null || end === null || start >= end) throw new Error(`bad Twosday hours: ${window.start} to ${window.end}`);
  if (!Number.isInteger(window.day) || window.day < 0 || window.day > 6) throw new Error(`bad Twosday day: ${window.day}`);

  // The wall clock in the window's time zone (a RangeError for an unknown zone).
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone: window.timeZone,
    weekday: "short",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hourCycle: "h23",
  }).formatToParts(now);
  const part = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  if (WEEKDAYS.indexOf(part("weekday")) !== window.day) return false;
  const seconds = Number(part("hour")) * 3600 + Number(part("minute")) * 60 + Number(part("second"));
  return seconds >= start && seconds < end;
}
