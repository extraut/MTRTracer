import type { SVGProps } from 'react';

type P = SVGProps<SVGSVGElement> & { size?: number };

function I({ size = 16, children, ...rest }: P & { children: React.ReactNode }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" strokeLinejoin="round" aria-hidden {...rest}>
      {children}
    </svg>
  );
}

export const Play = (p: P) => (
  <I {...p}>
    <path d="M4.5 3.2v9.6a.5.5 0 0 0 .76.43l7.7-4.8a.5.5 0 0 0 0-.86l-7.7-4.8a.5.5 0 0 0-.76.43Z" fill="currentColor" stroke="none" />
  </I>
);
export const Pause = (p: P) => (
  <I {...p}>
    <rect x="4" y="3" width="2.6" height="10" rx=".6" fill="currentColor" stroke="none" />
    <rect x="9.4" y="3" width="2.6" height="10" rx=".6" fill="currentColor" stroke="none" />
  </I>
);
export const Stop = (p: P) => (
  <I {...p}>
    <rect x="3.5" y="3.5" width="9" height="9" rx="1.2" fill="currentColor" stroke="none" />
  </I>
);
export const Restart = (p: P) => (
  <I {...p}>
    <path d="M2.8 8a5.2 5.2 0 1 0 1.6-3.75" />
    <path d="M2.6 2.6v2.9h2.9" />
  </I>
);
export const Sliders = (p: P) => (
  <I {...p}>
    <path d="M2.5 4.5h6M11.5 4.5h2M2.5 11.5h2M7.5 11.5h6" />
    <circle cx="10" cy="4.5" r="1.5" />
    <circle cx="6" cy="11.5" r="1.5" />
  </I>
);
export const Chevron = (p: P) => (
  <I {...p}>
    <path d="m4.5 6.5 3.5 3.5 3.5-3.5" />
  </I>
);
export const ChevronRight = (p: P) => (
  <I {...p}>
    <path d="m6.5 4.5 3.5 3.5-3.5 3.5" />
  </I>
);
export const Close = (p: P) => (
  <I {...p}>
    <path d="m4 4 8 8M12 4l-8 8" />
  </I>
);
export const Minimize = (p: P) => (
  <I {...p}>
    <path d="M3.5 8.5h9" />
  </I>
);
export const Maximize = (p: P) => (
  <I {...p}>
    <rect x="3.5" y="3.5" width="9" height="9" rx="1" />
  </I>
);
export const Restore = (p: P) => (
  <I {...p}>
    <rect x="3.5" y="5.5" width="7" height="7" rx="1" />
    <path d="M5.5 3.5h6a1 1 0 0 1 1 1v6" />
  </I>
);
export const Export = (p: P) => (
  <I {...p}>
    <path d="M8 2.5v7.5M5 5.5l3-3 3 3" />
    <path d="M3 9.5v3a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1v-3" />
  </I>
);
export const Globe = (p: P) => (
  <I {...p}>
    <circle cx="8" cy="8" r="5.5" />
    <path d="M2.5 8h11M8 2.5c1.6 1.6 2.3 3.5 2.3 5.5S9.6 11.9 8 13.5C6.4 11.9 5.7 10 5.7 8S6.4 4.1 8 2.5Z" />
  </I>
);
export const MapIcon = (p: P) => (
  <I {...p}>
    <path d="m2.5 4 3.5-1.5 4 1.5 3.5-1.5v9.5L10 13.5 6 12l-3.5 1.5V4Z" />
    <path d="M6 2.5V12M10 4v9.5" />
  </I>
);
export const Crosshair = (p: P) => (
  <I {...p}>
    <path d="M3 6V4a1 1 0 0 1 1-1h2M10 3h2a1 1 0 0 1 1 1v2M13 10v2a1 1 0 0 1-1 1h-2M6 13H4a1 1 0 0 1-1-1v-2" />
    <circle cx="8" cy="8" r="1.5" />
  </I>
);
export const Layers = (p: P) => (
  <I {...p}>
    <path d="m8 2.5 5.5 3L8 8.5l-5.5-3L8 2.5Z" />
    <path d="m2.5 8.2 5.5 3 5.5-3M2.5 10.8l5.5 3 5.5-3" />
  </I>
);
export const Search = (p: P) => (
  <I {...p}>
    <circle cx="7" cy="7" r="4" />
    <path d="m10 10 3.5 3.5" />
  </I>
);
export const Ethernet = (p: P) => (
  <I {...p}>
    <rect x="3" y="4.5" width="10" height="8" rx="1" />
    <path d="M5.5 12.5v-2M8 12.5v-2M10.5 12.5v-2M6 4.5V3h4v1.5" />
  </I>
);
export const Wifi = (p: P) => (
  <I {...p}>
    <path d="M2 6.2a8.6 8.6 0 0 1 12 0M4 8.6a5.7 5.7 0 0 1 8 0M6 11a2.8 2.8 0 0 1 4 0" />
    <circle cx="8" cy="13" r=".6" fill="currentColor" />
  </I>
);
export const Shield = (p: P) => (
  <I {...p}>
    <path d="M8 2.2 3 4v4c0 3 2.2 5 5 5.8 2.8-.8 5-2.8 5-5.8V4L8 2.2Z" />
    <rect x="6" y="7.2" width="4" height="3.3" rx=".6" />
    <path d="M6.8 7.2V6.4a1.2 1.2 0 0 1 2.4 0v.8" />
  </I>
);
export const Box = (p: P) => (
  <I {...p}>
    <path d="m8 2.5 5 2.7v5.6l-5 2.7-5-2.7V5.2l5-2.7Z" />
    <path d="M3 5.2 8 8l5-2.8M8 8v5.5" />
  </I>
);
export const Loop = (p: P) => (
  <I {...p}>
    <path d="M10.5 5.5h1a2.5 2.5 0 0 1 0 5h-7a2.5 2.5 0 0 1 0-5h1" />
    <path d="m7 3.5 2 2-2 2" />
  </I>
);
export const Cell = (p: P) => (
  <I {...p}>
    <path d="M3 12.5v-2M6 12.5V8M9 12.5V5.5M12 12.5V3" />
  </I>
);
export const Plug = (p: P) => (
  <I {...p}>
    <path d="M6 2.5v3M10 2.5v3M4.5 5.5h7v2.5a3.5 3.5 0 0 1-7 0V5.5ZM8 11.5v2" />
  </I>
);
export const Alert = (p: P) => (
  <I {...p}>
    <path d="M8 2.6 14 13H2L8 2.6Z" />
    <path d="M8 6.5v3" />
    <circle cx="8" cy="11.3" r=".5" fill="currentColor" />
  </I>
);
export const Info = (p: P) => (
  <I {...p}>
    <circle cx="8" cy="8" r="5.5" />
    <path d="M8 7.3v3.4" />
    <circle cx="8" cy="5.2" r=".5" fill="currentColor" />
  </I>
);
export const Check = (p: P) => (
  <I {...p}>
    <path d="m3.5 8.5 3 3 6-6.5" />
  </I>
);
export const Copy = (p: P) => (
  <I {...p}>
    <rect x="5.5" y="5.5" width="7.5" height="7.5" rx="1" />
    <path d="M10.5 5.5V4a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1v5.5a1 1 0 0 0 1 1h1.5" />
  </I>
);
export const Folder = (p: P) => (
  <I {...p}>
    <path d="M2.5 4.5a1 1 0 0 1 1-1h3l1.5 1.5h4.5a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-9a1 1 0 0 1-1-1v-7.5Z" />
  </I>
);
export const Route = (p: P) => (
  <I {...p}>
    <circle cx="3.8" cy="12" r="1.5" />
    <circle cx="12.2" cy="4" r="1.5" />
    <path d="M5.3 12h4.2a2.2 2.2 0 0 0 0-4.4h-3a2.2 2.2 0 0 1 0-4.4h4.2" />
  </I>
);
export const Pin = (p: P) => (
  <I {...p}>
    <path d="M8 14s4.5-4 4.5-7.5a4.5 4.5 0 1 0-9 0C3.5 10 8 14 8 14Z" />
    <circle cx="8" cy="6.5" r="1.5" />
  </I>
);
export const Columns = (p: P) => (
  <I {...p}>
    <rect x="2.5" y="3" width="11" height="10" rx="1" />
    <path d="M6.2 3v10M9.8 3v10" />
  </I>
);
export const Bolt = (p: P) => (
  <I {...p}>
    <path d="M9 2.5 4 9h4l-1 4.5L12 7H8l1-4.5Z" />
  </I>
);

export function AdapterIcon({ kind, ...p }: P & { kind: string }) {
  switch (kind) {
    case 'ethernet':
      return <Ethernet {...p} />;
    case 'wifi':
      return <Wifi {...p} />;
    case 'vpn':
      return <Shield {...p} />;
    case 'virtual':
      return <Box {...p} />;
    case 'loopback':
      return <Loop {...p} />;
    case 'cellular':
      return <Cell {...p} />;
    default:
      return <Plug {...p} />;
  }
}
