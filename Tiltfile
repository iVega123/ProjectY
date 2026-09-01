# ProjectY local development orchestration.
#
# The default profile loads the core topology. Pass `-- --full` to include the
# remaining data stores and application services declared with Compose's
# `full` profile.

config.define_bool(
    'full',
    usage = 'Start the complete topology, including telemetry, pricing, media, console, Cassandra, MongoDB, and MinIO.',
)
cfg = config.parse()
full = cfg.get('full', False)

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

profiles = ['full'] if full else []
docker_compose(
    'deploy/overlays/selfhost/compose.yaml',
    env_file = '.env',
    profiles = profiles,
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
        live_update = update_steps,
    )

configure_live_update(
    'projecty/api-gateway:dev',
    'services/api-gateway',
    ['services/api-gateway/Cargo.toml', 'services/api-gateway/Cargo.lock'],
    'cd /workspace && cargo fetch --locked',
    'cd /workspace && cargo build --locked',
)
configure_live_update(
    'projecty/rental-core:dev',
    'services/rental-core',
    ['services/rental-core/RentalCore.csproj'],
    'cd /workspace && dotnet restore RentalCore.csproj',
    'cd /workspace && dotnet build RentalCore.csproj --no-restore',
)
configure_live_update(
    'projecty/media-guard:dev',
    'services/media-guard',
    ['services/media-guard/Cargo.toml', 'services/media-guard/Cargo.lock'],
    'cd /workspace && cargo fetch --locked',
    'cd /workspace && cargo build --locked',
)
configure_live_update(
    'projecty/risk-pricing:dev',
    'services/risk-pricing',
    ['services/risk-pricing/pyproject.toml', 'services/risk-pricing/uv.lock'],
    'cd /workspace && python -m pip install --disable-pip-version-check -e .',
)
configure_live_update(
    'projecty/telemetry:dev',
    'services/telemetry',
    ['services/telemetry/mix.exs', 'services/telemetry/mix.lock'],
    'cd /workspace && mix deps.get',
    'cd /workspace && mix compile',
)
configure_live_update(
    'projecty/console:dev',
    'services/console',
    ['services/console/package.json', 'services/console/package-lock.json'],
    'cd /workspace && npm install --ignore-scripts',
)

bootstrap_script = ['pwsh', '-NoProfile', '-File', 'scripts/Initialize-LocalResource.ps1']
bootstrap_script_bat = [
    'powershell',
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    'scripts\\Initialize-LocalResource.ps1',
]

local_resource(
    'cockroach-migrations',
    bootstrap_script + ['-Resource', 'Cockroach'],
    cmd_bat = bootstrap_script_bat + ['-Resource', 'Cockroach'],
    deps = ['scripts/Initialize-LocalResource.ps1', 'deploy/db/cockroach'],
    resource_deps = ['cockroachdb'],
    labels = ['setup'],
)
local_resource(
    'kafka-topics',
    bootstrap_script + ['-Resource', 'Kafka'],
    cmd_bat = bootstrap_script_bat + ['-Resource', 'Kafka'],
    deps = ['scripts/Initialize-LocalResource.ps1', 'deploy/kafka/topics.txt'],
    resource_deps = ['kafka'],
    labels = ['setup'],
)

if full:
    local_resource(
        'cassandra-schema',
        bootstrap_script + ['-Resource', 'Cassandra'],
        cmd_bat = bootstrap_script_bat + ['-Resource', 'Cassandra'],
        deps = ['scripts/Initialize-LocalResource.ps1', 'deploy/db/cassandra'],
        resource_deps = ['cassandra'],
        labels = ['setup'],
    )
    local_resource(
        'minio-buckets',
        bootstrap_script + ['-Resource', 'MinIO'],
        cmd_bat = bootstrap_script_bat + ['-Resource', 'MinIO'],
        deps = ['scripts/Initialize-LocalResource.ps1', 'deploy/minio/buckets.txt'],
        resource_deps = ['minio'],
        labels = ['setup'],
    )

infra_resources = ['cockroachdb', 'cockroach-init', 'redis', 'rabbitmq', 'kafka']
observability_resources = ['otel-collector', 'prometheus', 'tempo', 'loki']
service_resources = ['api-gateway', 'rental-core']

if full:
    infra_resources += ['cassandra', 'mongodb', 'minio']
    service_resources += ['media-guard', 'risk-pricing', 'telemetry']

for resource in infra_resources:
    dc_resource(resource, labels = ['infra'])

for resource in observability_resources:
    dc_resource(resource, labels = ['observability'])

service_setup_dependencies = {
    'rental-core': ['cockroach-migrations', 'kafka-topics'],
    'media-guard': ['minio-buckets'],
    'risk-pricing': ['kafka-topics'],
    'telemetry': ['cassandra-schema', 'kafka-topics'],
}

for resource in service_resources:
    dc_resource(
        resource,
        labels = ['services'],
        resource_deps = service_setup_dependencies.get(resource, []),
    )

dc_resource('toxiproxy', labels = ['drills'])

dc_resource(
    'grafana',
    labels = ['observability'],
    links = [link('http://localhost:3001', 'Grafana')],
)

if full:
    dc_resource(
        'console',
        labels = ['services'],
        links = [link('http://localhost:3000', 'Console')],
    )

print('ProjectY profile: %s' % ('full' if full else 'core'))
print('Tilt UI:  http://localhost:10350')
print('Grafana:  http://localhost:3001')
print('Console:  http://localhost:3000 (full profile)')
