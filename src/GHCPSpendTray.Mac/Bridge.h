#ifndef GHCP_BRIDGE_H
#define GHCP_BRIDGE_H

// Strings are UTF-8 JSON. Every returned string must be released with ghcp_free.
// Call from one frontend thread; work and platform callbacks are asynchronous.
char *ghcp_request(const char *json);
char *ghcp_poll(void);
void ghcp_free(char *json);
void ghcp_shutdown(void);

#endif
