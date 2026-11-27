// ARIA Notify (Chromium 141, Firefox 150) is not in the TypeScript DOM lib yet. Optional, so a
// caller has to allow for an older browser.
interface AriaNotificationOptions {
  priority?: 'normal' | 'high';
}

interface Document {
  ariaNotify?(announcement: string, options?: AriaNotificationOptions): void;
}

interface Element {
  ariaNotify?(announcement: string, options?: AriaNotificationOptions): void;
}
