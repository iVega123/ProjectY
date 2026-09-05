# ProjectY local development orchestration.
#
# The audited services remain the active topology while the strangler migration
# replaces them one task at a time. The gateway is the first modernization
# service and is the only public entry point added by this epic.

local_env_exists = os.path.exists('.env')
rabbitmq_definitions_exist = os.path.exists('.rabbitmq-definitions.json')

if local_env_exists != rabbitmq_definitions_exist:
    existing_local_file = '.env' if local_env_exists else '.rabbitmq-definitions.json'
    missing_local_file = '.rabbitmq-definitions.json' if local_env_exists else '.env'
    partial_credentials_message = (
        'Partial local credential set: %s exists but %s is missing. Tilt will not ' +
        'rotate credentials automatically because persistent volumes may still use them. ' +
        'Stop the stack, remove volumes initialized with the old credentials, then run ' +
        '`powershell -ExecutionPolicy Bypass -File scripts/New-LocalSecrets.ps1 -Force`.'
    ) % (existing_local_file, missing_local_file)
    fail(partial_credentials_message)

required_local_files = ['.env', '.rabbitmq-definitions.json']
missing_local_files = [path for path in required_local_files if not os.path.exists(path)]
if missing_local_files and config.tilt_subcommand in ['up', 'ci']:
    print('Generating ignored local credentials for the first run...')
    local(
        ['pwsh', '-NoProfile', '-File', 'scripts/New-LocalSecrets.ps1'],
        command_bat = [
            'powershell',
            '-NoProfile',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            'scripts\\New-LocalSecrets.ps1',
        ],
        echo_off = True,
    )

missing_local_files = [path for path in required_local_files if not os.path.exists(path)]
if missing_local_files:
    missing_files_message = (
        'Local credential generation did not create: %s. Run ' +
        '`powershell -ExecutionPolicy Bypass -File scripts/New-LocalSecrets.ps1` ' +
        'to inspect the underlying error.'
    ) % ', '.join(missing_local_files)
    fail(missing_files_message)

docker_compose(
    ['docker-compose.yml', 'docker-compose.chaos.yml'],
    env_file = '.env',
    project_name = 'projecty',
)

# Docker Compose workloads use restart_container(); the restart_process
# extension does not support Compose resources. Development images keep their
# source and language toolchain under /workspace so incremental builds happen
# in-place instead of rebuilding an image.
def configure_live_update(image, context, manifests, install_command, build_command = ''):
    if not os.path.exists(context):
        print('Live update pending service source: %s' % context)
        return

    update_steps = [
        fall_back_on(context + '/Dockerfile'),
        sync(context, '/workspace'),
        run(install_command, trigger = manifests),
    ]
    if build_command:
        update_steps.append(run(build_command))
    update_steps.append(restart_container())

    docker_build(
        image,
        context,
        dockerfile = context + '/Dockerfile',
        target = 'development',
        live_update = update_steps,
    )

configure_live_update(
    'projecty/api-gateway:dev',
    'services/api-gateway',
    ['services/api-gateway/Cargo.toml', 'services/api-gateway/Cargo.lock'],
    'cd /workspace && cargo fetch --locked',
    'cd /workspace && cargo build --locked',
)
infra_resources = ['toxiproxy', 'postgres', 'redis', 'rabbitmq', 'mongodb', 'minio']
observability_resources = ['tempo', 'loki', 'otel-collector', 'prometheus', 'grafana']
setup_resources = ['auth-gate-migrations', 'rider-manager-migrations', 'moto-hub-migrations']
service_resources = ['auth-gate', 'rider-manager', 'moto-hub', 'rental-operations']

for resource in infra_resources:
    dc_resource(resource, labels = ['infra'])

for resource in observability_resources:
    resource_links = []
    if resource == 'grafana':
        resource_links = [link('http://localhost:3000', 'Grafana')]
    dc_resource(resource, labels = ['observability'], links = resource_links)

for resource in setup_resources:
    dc_resource(resource, labels = ['setup'])

for resource in service_resources:
    dc_resource(
        resource,
        labels = ['services'],
        resource_deps = observability_resources,
    )

dc_resource('pgadmin', labels = ['tools'], links = [link('http://localhost:5050', 'pgAdmin')])
dc_resource(
    'api-gateway',
    labels = ['services'],
    links = [link('http://localhost:8090/health/ready', 'Gateway')],
    resource_deps = observability_resources,
)

print('Tilt UI:  http://localhost:10350')
print('Gateway:  http://localhost:8090')
print('Grafana:  http://localhost:3000')

# Keep unavailable target-service drills visible and explicitly disabled.
load('ext://uibutton', 'cmd_button')
chaos_shell = 'powershell' if os.name == 'nt' else 'pwsh'
chaos_prefix = [chaos_shell, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts/Invoke-ChaosDrill.ps1']
for drill in read_json('deploy/chaos/drills.json'):
    cmd_button(
        'chaos-' + drill['id'],
        resource = 'toxiproxy',
        argv = chaos_prefix + [drill['id']],
        text = drill['label'],
        disabled = not drill['available'],
    )
    cmd_button(
        'chaos-clear-' + drill['id'],
        resource = 'toxiproxy',
        argv = chaos_prefix + [drill['id'], '-Clear'],
        text = 'Clear: ' + drill['label'],
        disabled = not drill['available'],
    )
