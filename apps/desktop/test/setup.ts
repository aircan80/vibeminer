import { configure } from '@testing-library/react';
// findBy* and waitFor are deadlock guards, not performance claims (#104). Their 1 s default is wall time, and these
// tests are CPU-bound jsdom renders and role queries: a wait that settles in 100 ms on a quiet machine took over 3 s
// with other gates running. Keep this below testTimeout in vite.config.ts so a stuck wait reports what it waited for.
configure({ asyncUtilTimeout: 20_000 });
