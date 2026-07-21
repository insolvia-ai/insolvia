import { createRequestHandler } from "@react-router/architect";

// The React Router server build is produced by `react-router build` into
// build/server/. It is copied alongside this handler in the Lambda image.
import * as build from "../build/server/index.js";

export const handler = createRequestHandler({ build });
