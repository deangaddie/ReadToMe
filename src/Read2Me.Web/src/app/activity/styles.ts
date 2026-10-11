import { adoptStyles } from '@app/core/styles';
import activityCss from './activity.css' with { type: 'text' };

// The activity centre's stylesheet, adopted once however many of its elements are imported.
adoptStyles(activityCss);
