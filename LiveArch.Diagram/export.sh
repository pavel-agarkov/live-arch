unset DOCKER_HOST
docker context use default
docker run -it --rm -v $(pwd -W):/usr/local/structurizr structurizr/structurizr export --format json --workspace workspace.dsl
cp workspace.json ./1