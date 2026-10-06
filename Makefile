.PHONY: build test test-e2e test-http test-own-host test-browser browser-install verify
build:
	dotnet build Burbuja.slnx -c Release -m:1
test: build
	python3 scripts/test-kernel.py
test-e2e:
	python3 scripts/test-e2e.py all
test-http:
	python3 scripts/test-e2e.py http
test-own-host:
	python3 scripts/test-e2e.py own-host
test-browser:
	python3 scripts/test-e2e.py browser
browser-install:
	python3 scripts/test-e2e.py install-browser
verify: test test-e2e
