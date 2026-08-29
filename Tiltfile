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
    'deploy/compose.yaml',
    env_file = '.env',
    profiles = profiles,
    project_name = 'projecty',
)

infra_resources = ['cockroachdb', 'redis', 'rabbitmq', 'kafka']
observability_resources = ['otel-collector', 'prometheus', 'tempo', 'loki']
service_resources = ['api-gateway', 'rental-core']

if full:
    infra_resources += ['cassandra', 'mongodb', 'minio']
    service_resources += ['media-guard', 'risk-pricing', 'telemetry']

for resource in infra_resources:
    dc_resource(resource, labels = ['infra'])

for resource in observability_resources:
    dc_resource(resource, labels = ['observability'])

for resource in service_resources:
    dc_resource(resource, labels = ['services'])

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
