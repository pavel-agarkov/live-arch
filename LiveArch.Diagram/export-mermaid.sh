unset DOCKER_HOST
docker context use default
docker run -it --rm -v $(pwd -W):/usr/local/structurizr structurizr/structurizr export --format mermaid -output mermaid --workspace workspace.dsl