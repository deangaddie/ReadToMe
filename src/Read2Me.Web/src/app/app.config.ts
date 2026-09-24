import { provideHttpClient, withFetch } from '@angular/common/http';
import {
  ApplicationConfig,
  ErrorHandler,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideEnvironmentInitializer,
} from '@angular/core';
import { MatIconRegistry } from '@angular/material/icon';
import { provideRouter, withComponentInputBinding, withRouterConfig } from '@angular/router';
import { AiServicesStore } from './ai-services/ai-services-store';
import { ApiErrorHandler } from './api/api-error-handler';
import { routes } from './app.routes';
import { LiveService } from './live/live.service';
import { ThemeService } from './theme/theme.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withFetch()),
    // Uncaught ApiError rejections become error toasts (ticket 05 / design §8).
    { provide: ErrorHandler, useClass: ApiErrorHandler },
    provideRouter(
      routes,
      withComponentInputBinding(),
      // Child routes inherit `data` (RouteMeta) and the `folder` param; the shell relies on it.
      withRouterConfig({ paramsInheritanceStrategy: 'always' }),
    ),
    // Self-hosted Material Symbols Rounded (styles.scss) instead of the Material Icons CDN font.
    provideEnvironmentInitializer(() =>
      inject(MatIconRegistry).setDefaultFontSetClass('material-symbols-rounded'),
    ),
    // Apply the shared theme before first paint; an unreachable host keeps the compiled defaults.
    provideAppInitializer(() => inject(ThemeService).load()),
    // Open the live hub once for the app's lifetime (ticket 07); it reconnects on its own.
    provideEnvironmentInitializer(() => inject(LiveService).start()),
    // The AI services store listens from the start so the watchdog log misses nothing (ticket 25).
    provideEnvironmentInitializer(() => void inject(AiServicesStore)),
  ],
};
