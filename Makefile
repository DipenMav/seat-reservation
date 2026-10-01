URL ?= http://localhost:8080
ARGS ?=

.PHONY: up down logs test run burst

up:            ## build and start api + postgres (same image as production)
	docker compose up --build -d
	@echo "waiting for readiness..."; for i in $$(seq 1 60); do curl -sf $(URL)/health/ready >/dev/null && echo ready && exit 0; sleep 2; done; exit 1

down:
	docker compose down -v

logs:
	docker compose logs -f api

test:          ## integration + concurrency tests (needs Docker for Testcontainers, or TEST_DATABASE_URL)
	dotnet test

run:           ## run the API on the host against ConnectionStrings__Default / DATABASE_URL
	dotnet run --project src/SeatReservation.Api

burst:         ## make burst URL=https://your-app.example.com ARGS="--requests 20000"
	./burst.sh $(URL) $(ARGS)
