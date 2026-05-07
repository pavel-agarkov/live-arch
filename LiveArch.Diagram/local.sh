unset DOCKER_HOST
docker context use default
docker run -it --rm -p 8181:8080 -v $(pwd -W):/usr/local/structurizr structurizr/structurizr local