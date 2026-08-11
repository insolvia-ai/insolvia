import { createContext, useContext } from "react";

import type { AdminClient } from "./client";

export const ClientContext = createContext<AdminClient | null>(null);

export function useClient(): AdminClient {
  const client = useContext(ClientContext);
  if (client === null) {
    throw new Error("useClient requires the app's ClientContext provider");
  }
  return client;
}
