import env from "../env/env";

// With FFA_WEEKEND_ONLY on, the public Free For All queue is open from Friday
// 12:00am to Sunday 11:59pm US Eastern. Intl handles the EST/EDT switch.
const EASTERN = "America/New_York";
const SUNDAY_CLOSE_MINUTE = 23 * 60 + 59;

export const FFA_SCHEDULE_TEXT = "Free For All is open Friday 12:00am to Sunday 11:59pm Eastern.";

const easternClock = new Intl.DateTimeFormat("en-US", {
  timeZone: EASTERN,
  weekday: "short",
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
});

export function isFfaQueueOpen(now: Date = new Date(), weekendOnly: boolean = env.FFA_WEEKEND_ONLY): boolean {
  if (!weekendOnly) return true;
  const parts = easternClock.formatToParts(now);
  const part = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  const weekday = part("weekday");
  const minuteOfDay = Number(part("hour")) * 60 + Number(part("minute"));
  if (weekday === "Fri" || weekday === "Sat") return true;
  if (weekday === "Sun") return minuteOfDay < SUNDAY_CLOSE_MINUTE;
  return false;
}

/** Hydra-style action failure for an FFA request outside the weekend window. */
export function ffaQueueClosedFailure() {
  return {
    body: {
      error: "ffa_queue_closed",
      ErrorCode: "QueueClosed",
      ErrorMessage: FFA_SCHEDULE_TEXT,
    },
    metadata: null,
    return_code: 1,
  };
}
