#!/bin/bash

# Make sure Docker is running
if ! docker info > /dev/null 2>&1; then
  echo "❌ Docker is not running. Please start Docker Desktop first."
  exit 1
fi

echo "🛠️  Building and starting MatchPredictor container..."
docker-compose up --build
