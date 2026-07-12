// Browser notification plumbing — the delivery-to-a-human layer the daemon
// deliberately leaves to clients. Tier 1: works while the tab is open.

export type NotifyPermission = "unsupported" | "default" | "granted" | "denied";

export function notifyPermission(): NotifyPermission {
  if (!("Notification" in window)) return "unsupported";
  return Notification.permission;
}

export async function requestNotifyPermission(): Promise<NotifyPermission> {
  if (!("Notification" in window)) return "unsupported";
  return await Notification.requestPermission();
}

/** Fires a desktop notification when permitted; a no-op otherwise. */
export function fireNotification(title: string, body: string, onClick?: () => void): void {
  if (!("Notification" in window) || Notification.permission !== "granted") return;
  const notification = new Notification(title, { body, tag: "orkis-approval" });
  if (onClick) {
    notification.onclick = () => {
      window.focus();
      onClick();
      notification.close();
    };
  }
}
