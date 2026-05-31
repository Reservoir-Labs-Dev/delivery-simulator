import React, { useEffect, useMemo, useRef, useState } from 'react';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import './App.css';

// SignalR hub URL — overrideable at build time via REACT_APP_HUB_URL.
// Defaults to localhost:5000/hubs/orders which is what docker-compose
// exposes for the dashboard-api container.
const HUB_URL =
  process.env.REACT_APP_HUB_URL || 'http://localhost:5000/hubs/orders';

const STATUS_TONES = {
  CREATED:              'blue',
  PAYMENT_PROCESSING:   'amber',
  PAYMENT_SUCCEEDED:    'amber',
  PAYMENT_FAILED:       'coral',
  KITCHEN_PREPARING:    'teal',
  ORDER_READY:          'teal',
  DELIVERY_IN_PROGRESS: 'purple',
  DELIVERED:            'accent',
  DELIVERY_FAILED:      'coral',
  DEAD_LETTERED:        'coral',
};

const PIPELINE_SERVICES = [
  { name: 'OrderService',    port: '5294', color: 'blue'   },
  { name: 'PaymentService',  port: '5203', color: 'amber'  },
  { name: 'KitchenService',  port: '5233', color: 'teal'   },
  { name: 'DeliveryService', port: '5097', color: 'purple' },
];

function App() {
  const [connectionState, setConnectionState] = useState('Connecting');
  const [events, setEvents] = useState([]);
  const connectionRef = useRef(null);

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(HUB_URL)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    connectionRef.current = connection;

    connection.on('OrderStatusChanged', (notification) => {
      setEvents((prev) => [
        {
          id: `${notification.orderId}-${notification.updatedAt}-${notification.status}`,
          receivedAt: new Date(),
          ...notification,
        },
        ...prev,
      ].slice(0, 200));
    });

    connection.onreconnecting(() => setConnectionState('Reconnecting'));
    connection.onreconnected(() => setConnectionState('Connected'));
    connection.onclose(() => setConnectionState('Disconnected'));

    connection
      .start()
      .then(() => setConnectionState('Connected'))
      .catch((err) => {
        console.error('SignalR connection failed', err);
        setConnectionState('Disconnected');
      });

    return () => {
      connectionRef.current = null;
      connection.stop().catch(() => { /* swallow on unmount */ });
    };
  }, []);

  const connectionTone = useMemo(() => {
    switch (connectionState) {
      case 'Connected':    return 'ok';
      case 'Reconnecting': return 'warn';
      default:             return 'err';
    }
  }, [connectionState]);

  const ordersSeen = useMemo(
    () => new Set(events.map((e) => e.orderId)).size,
    [events],
  );

  return (
    <div className="app">
      <header className="header">
        <div className="header-inner">
          <div className="logo">
            <span className={`logo-dot logo-dot--${connectionTone}`} />
            <span className="logo-text">Reservoir Labs</span>
          </div>
          <div className="header-tag">Order Processing Dashboard</div>
        </div>
      </header>

      <main className="main">
        <section className="hero">
          <div className="hero-meta">DOG-35 · live event log</div>
          <h1 className="hero-title">
            Live order<br />
            <span className="hero-accent">events.</span>
          </h1>
          <p className="hero-desc">
            Streaming <code>OrderStatusChanged</code> notifications from
            dashboard-api's SignalR hub. POST an order against{' '}
            <code>localhost:5294/orders</code> to see it flow through the
            pipeline.
          </p>

          <div className="status-grid">
            <div className="status-item">
              <div className="status-label">React</div>
              <div className="status-value ok">running</div>
            </div>
            <div className="status-item">
              <div className="status-label">SignalR hub</div>
              <div className={`status-value status-value--${connectionTone}`}>
                {connectionState.toLowerCase()}
              </div>
            </div>
            <div className="status-item">
              <div className="status-label">Events received</div>
              <div className="status-value">{events.length}</div>
            </div>
            <div className="status-item">
              <div className="status-label">Distinct orders</div>
              <div className="status-value">{ordersSeen}</div>
            </div>
          </div>
        </section>

        <section className="pipeline">
          <div className="pipeline-label">Pipeline services</div>
          <div className="pipeline-row">
            {PIPELINE_SERVICES.map((svc, i) => (
              <React.Fragment key={svc.name}>
                <div className={`pipeline-node pipeline-node--${svc.color}`}>
                  <div className="pipeline-node-name">{svc.name}</div>
                  <div className="pipeline-node-port">:{svc.port}</div>
                </div>
                {i < PIPELINE_SERVICES.length - 1 && (
                  <div className="pipeline-arrow">→</div>
                )}
              </React.Fragment>
            ))}
          </div>
        </section>

        <section className="event-log">
          <div className="event-log-header">
            <div className="event-log-label">Event log</div>
            <button
              type="button"
              className="event-log-clear"
              onClick={() => setEvents([])}
              disabled={events.length === 0}
            >
              clear
            </button>
          </div>

          {events.length === 0 ? (
            <div className="event-log-empty">
              {connectionState === 'Connected'
                ? 'Waiting for events… POST an order to see them appear here.'
                : connectionState === 'Reconnecting'
                  ? 'Reconnecting to hub…'
                  : `Not connected. Is dashboard-api running at ${HUB_URL}?`}
            </div>
          ) : (
            <ul className="event-list">
              {events.map((e) => (
                <li className="event-row" key={e.id}>
                  <span className="event-time">
                    {e.receivedAt.toLocaleTimeString(undefined, { hour12: false })}
                    .{String(e.receivedAt.getMilliseconds()).padStart(3, '0')}
                  </span>
                  <span className="event-source">{e.sourceService}</span>
                  <span
                    className={`event-status event-status--${STATUS_TONES[e.status] || 'muted'}`}
                  >
                    {e.status}
                  </span>
                  <span className="event-order">{shortOrderId(e.orderId)}</span>
                  {e.retryCount > 0 && (
                    <span className="event-attempt">retry {e.retryCount}</span>
                  )}
                  {e.outcome && e.outcome !== 'SUCCESS' && (
                    <span className={`event-outcome event-outcome--${e.outcome.toLowerCase()}`}>
                      {e.outcome}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>
      </main>

      <footer className="footer">
        <span>Reservoir Labs · KIU Bachelor Thesis 2026</span>
        <span>Defense July 2026</span>
      </footer>
    </div>
  );
}

function shortOrderId(orderId) {
  if (!orderId) return '';
  return orderId.replace(/-/g, '').slice(0, 8);
}

export default App;
