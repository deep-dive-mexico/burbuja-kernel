.PHONY: build test
build:
	dotnet build Burbuja.slnx -c Release -m:1
test: build
	python3 scripts/test-kernel.py
