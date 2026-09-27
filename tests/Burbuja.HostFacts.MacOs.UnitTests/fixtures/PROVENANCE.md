# Synthetic HostFacts parser fixtures

Every .txt file in this exported directory is authored synthetic command output. No device, account, tailnet, battery serial, or captured machine dump was copied from the historical integration checkout. Numeric values exercise parser edge cases; they are not readings from a real host. Documentation IPs use 2001:db8::/32; example hosts use .example. The Tailscale local node is first and another peer is offline. Battery gauges include the 65535 unavailable sentinel. Filesystem fixtures exercise space-bearing volume names and a zero-valued auto_home row.
