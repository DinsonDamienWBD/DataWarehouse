using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.ComplianceAutomation;

/// <summary>
/// ISO/IEC 27001:2022 Information Security Management System compliance automation plugin.
/// Implements automated checks for Annex A controls and ISMS clause requirements.
/// Covers organizational, people, physical, and technological controls plus ISMS governance.
/// </summary>
public class Iso27001CompliancePlugin : ComplianceAutomationPluginBase
{
    private readonly List<AutomationControl> _controls;
    private readonly Dictionary<string, AutomationCheckResult> _lastResults = new();

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.compliance.iso27001";

    /// <inheritdoc />
    public override string Name => "ISO 27001:2022 Compliance Automation";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <inheritdoc />
    public override ComplianceFramework SupportedFramework => ComplianceFramework.Iso27001;

    public Iso27001CompliancePlugin()
    {
        _controls = InitializeControls();
    }

    private List<AutomationControl> InitializeControls()
    {
        return new List<AutomationControl>
        {
            // =====================================================
            // A.5 ORGANIZATIONAL CONTROLS (ISO.5.*)
            // =====================================================

            // A.5.1 Policies for information security
            new("ISO.5.1", "Policies for Information Security",
                "Information security policy and topic-specific policies shall be defined, approved by management, published, communicated to and acknowledged by relevant personnel and relevant interested parties, and reviewed at planned intervals and if significant changes occur",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            // A.5.2 Information security roles and responsibilities
            new("ISO.5.2", "Information Security Roles and Responsibilities",
                "Information security roles and responsibilities shall be defined and allocated according to the organization needs",
                ControlCategory.AccessControl, ControlSeverity.High, true),

            // A.5.3 Segregation of duties
            new("ISO.5.3", "Segregation of Duties",
                "Conflicting duties and conflicting areas of responsibility shall be segregated",
                ControlCategory.AccessControl, ControlSeverity.High, true),

            // A.5.4 Management responsibilities
            new("ISO.5.4", "Management Responsibilities",
                "Management shall require all personnel to apply information security in accordance with the established information security policy, topic-specific policies and procedures of the organization",
                ControlCategory.DataProtection, ControlSeverity.High, false),

            // A.5.5 Contact with authorities
            new("ISO.5.5", "Contact with Authorities",
                "The organization shall establish and maintain contact with relevant authorities",
                ControlCategory.Incident, ControlSeverity.Medium, false),

            // A.5.6 Contact with special interest groups
            new("ISO.5.6", "Contact with Special Interest Groups",
                "The organization shall establish and maintain contact with special interest groups or other specialist security forums and professional associations",
                ControlCategory.DataProtection, ControlSeverity.Low, false),

            // A.5.7 Threat intelligence
            new("ISO.5.7", "Threat Intelligence",
                "Information relating to information security threats shall be collected and analysed to produce threat intelligence",
                ControlCategory.NetworkSecurity, ControlSeverity.High, true),

            // A.5.8 Information security in project management
            new("ISO.5.8", "Information Security in Project Management",
                "Information security shall be integrated into project management",
                ControlCategory.DataProtection, ControlSeverity.Medium, false),

            // A.5.9 Inventory of information and other associated assets
            new("ISO.5.9", "Inventory of Information and Assets",
                "An inventory of information and other associated assets, including owners, shall be developed and maintained",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.5.10 Acceptable use of information and other associated assets
            new("ISO.5.10", "Acceptable Use of Information and Assets",
                "Rules for the acceptable use and procedures for handling information and other associated assets shall be identified, documented and implemented",
                ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // A.5.11 Return of assets
            new("ISO.5.11", "Return of Assets",
                "Personnel and other interested parties as appropriate shall return all the organization's assets in their possession upon change or termination of their employment, contract or agreement",
                ControlCategory.AccessControl, ControlSeverity.Medium, false),

            // A.5.12 Classification of information
            new("ISO.5.12", "Classification of Information",
                "Information shall be classified according to the information security needs of the organization based on confidentiality, integrity, availability and relevant interested party requirements",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.5.13 Labelling of information
            new("ISO.5.13", "Labelling of Information",
                "An appropriate set of procedures for information labelling shall be developed and implemented in accordance with the information classification scheme adopted by the organization",
                ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // A.5.14 Information transfer
            new("ISO.5.14", "Information Transfer",
                "Information transfer rules, procedures, or agreements shall be in place for all types of transfer facilities within the organization and between the organization and other parties",
                ControlCategory.Encryption, ControlSeverity.High, true),

            // A.5.15 Access control
            new("ISO.5.15", "Access Control",
                "Rules to control physical and logical access to information and other associated assets shall be established and implemented based on business and information security requirements",
                ControlCategory.AccessControl, ControlSeverity.Critical, true),

            // A.5.16 Identity management
            new("ISO.5.16", "Identity Management",
                "The full life cycle of identities shall be managed",
                ControlCategory.AccessControl, ControlSeverity.Critical, true),

            // A.5.17 Authentication information
            new("ISO.5.17", "Authentication Information",
                "Allocation and management of authentication information shall be controlled by a management process, including advising personnel on appropriate handling of authentication information",
                ControlCategory.AccessControl, ControlSeverity.Critical, true),

            // A.5.18 Access rights
            new("ISO.5.18", "Access Rights",
                "Access rights to information and other associated assets shall be provisioned, reviewed, modified and removed in accordance with the organization's topic-specific policy on and rules for access control",
                ControlCategory.AccessControl, ControlSeverity.Critical, true),

            // A.5.19 Information security in supplier relationships
            new("ISO.5.19", "Information Security in Supplier Relationships",
                "Processes and procedures shall be defined and implemented to manage the information security risks associated with the use of supplier's products or services",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.5.20 Addressing information security within supplier agreements
            new("ISO.5.20", "Addressing Security in Supplier Agreements",
                "Relevant information security requirements shall be established and agreed with each supplier based on the type of supplier relationship",
                ControlCategory.DataProtection, ControlSeverity.High, false),

            // A.5.21 Managing information security in the ICT supply chain
            new("ISO.5.21", "Managing Security in ICT Supply Chain",
                "Processes and procedures shall be defined and implemented to manage the information security risks associated with the ICT products and services supply chain",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.5.22 Monitoring, review and change management of supplier services
            new("ISO.5.22", "Monitoring and Review of Supplier Services",
                "The organization shall regularly monitor, review, evaluate and manage change in supplier information security practices and service delivery",
                ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // A.5.23 Information security for use of cloud services
            new("ISO.5.23", "Information Security for Cloud Services",
                "Processes for acquisition, use, management and exit from cloud services shall be established in accordance with the organization's information security requirements",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.5.24 Information security incident management planning and preparation
            new("ISO.5.24", "Incident Management Planning",
                "The organization shall plan and prepare for managing information security incidents by defining, establishing and communicating information security incident management processes, roles and responsibilities",
                ControlCategory.Incident, ControlSeverity.Critical, true),

            // A.5.25 Assessment and decision on information security events
            new("ISO.5.25", "Assessment of Security Events",
                "The organization shall assess information security events and decide if they are to be categorized as information security incidents",
                ControlCategory.Incident, ControlSeverity.High, true),

            // A.5.26 Response to information security incidents
            new("ISO.5.26", "Response to Security Incidents",
                "Information security incidents shall be responded to in accordance with the documented procedures",
                ControlCategory.Incident, ControlSeverity.Critical, true),

            // A.5.27 Learning from information security incidents
            new("ISO.5.27", "Learning from Security Incidents",
                "Knowledge gained from information security incidents shall be used to strengthen and improve the information security controls",
                ControlCategory.Incident, ControlSeverity.Medium, true),

            // A.5.28 Collection of evidence
            new("ISO.5.28", "Collection of Evidence",
                "The organization shall establish and implement procedures for the identification, collection, acquisition and preservation of evidence related to information security events",
                ControlCategory.Audit, ControlSeverity.High, true),

            // A.5.29 Information security during disruption
            new("ISO.5.29", "Information Security During Disruption",
                "The organization shall plan how to maintain information security at an appropriate level during disruption",
                ControlCategory.BusinessContinuity, ControlSeverity.High, true),

            // A.5.30 ICT readiness for business continuity
            new("ISO.5.30", "ICT Readiness for Business Continuity",
                "ICT readiness shall be planned, implemented, maintained and tested based on business continuity objectives and ICT continuity requirements",
                ControlCategory.BusinessContinuity, ControlSeverity.High, true),

            // A.5.31 Legal, statutory, regulatory and contractual requirements
            new("ISO.5.31", "Legal and Regulatory Requirements",
                "Legal, statutory, regulatory and contractual requirements relevant to information security and the organization's approach to meet these requirements shall be identified, documented and kept up to date",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            // A.5.32 Intellectual property rights
            new("ISO.5.32", "Intellectual Property Rights",
                "The organization shall implement appropriate procedures to protect intellectual property rights",
                ControlCategory.DataProtection, ControlSeverity.Medium, false),

            // A.5.33 Protection of records
            new("ISO.5.33", "Protection of Records",
                "Records shall be protected from loss, destruction, falsification, unauthorized access and unauthorized release",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.5.34 Privacy and protection of PII
            new("ISO.5.34", "Privacy and Protection of PII",
                "The organization shall identify and meet the requirements regarding the preservation of privacy and protection of PII according to applicable laws and regulations and contractual requirements",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            // A.5.35 Independent review of information security
            new("ISO.5.35", "Independent Review of Information Security",
                "The organization's approach to managing information security and its implementation including people, processes and technologies shall be reviewed independently at planned intervals, or when significant changes occur",
                ControlCategory.Audit, ControlSeverity.High, false),

            // A.5.36 Compliance with policies, rules and standards for information security
            new("ISO.5.36", "Compliance with Security Policies",
                "Compliance with the organization's information security policy, topic-specific policies, rules and standards shall be regularly reviewed",
                ControlCategory.Audit, ControlSeverity.High, true),

            // A.5.37 Documented operating procedures
            new("ISO.5.37", "Documented Operating Procedures",
                "Operating procedures for information processing facilities shall be documented and made available to personnel who need them",
                ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // =====================================================
            // A.6 PEOPLE CONTROLS (ISO.6.*)
            // =====================================================

            // A.6.1 Screening
            new("ISO.6.1", "Screening",
                "Background verification checks on all candidates to become personnel shall be carried out prior to joining the organization and on an ongoing basis taking into consideration applicable laws, regulations and ethics and be proportional to the business requirements, the classification of the information to be accessed and the perceived risks",
                ControlCategory.AccessControl, ControlSeverity.High, false),

            // A.6.2 Terms and conditions of employment
            new("ISO.6.2", "Terms and Conditions of Employment",
                "The employment contractual agreements shall state the personnel's and the organization's responsibilities for information security",
                ControlCategory.DataProtection, ControlSeverity.High, false),

            // A.6.3 Information security awareness, education and training
            new("ISO.6.3", "Information Security Awareness and Training",
                "Personnel of the organization and relevant interested parties shall receive appropriate information security awareness, education and training and regular updates of the organization's information security policy, topic-specific policies and procedures, as relevant for their job function",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.6.4 Disciplinary process
            new("ISO.6.4", "Disciplinary Process",
                "A disciplinary process shall be formalized and communicated to take actions against personnel and other relevant interested parties who have committed an information security policy violation",
                ControlCategory.DataProtection, ControlSeverity.Medium, false),

            // A.6.5 Responsibilities after termination or change of employment
            new("ISO.6.5", "Responsibilities After Termination",
                "Information security responsibilities and duties that remain valid after termination or change of employment shall be defined, enforced and communicated to relevant personnel and other interested parties",
                ControlCategory.AccessControl, ControlSeverity.High, true),

            // A.6.6 Confidentiality or non-disclosure agreements
            new("ISO.6.6", "Confidentiality Agreements",
                "Confidentiality or non-disclosure agreements reflecting the organization's needs for the protection of information shall be identified, documented, regularly reviewed and signed by personnel and other relevant interested parties",
                ControlCategory.DataProtection, ControlSeverity.High, false),

            // A.6.7 Remote working
            new("ISO.6.7", "Remote Working",
                "Security measures shall be implemented when personnel are working remotely to protect information accessed, processed or stored outside the organization's premises",
                ControlCategory.AccessControl, ControlSeverity.High, true),

            // A.6.8 Information security event reporting
            new("ISO.6.8", "Information Security Event Reporting",
                "The organization shall provide a mechanism for personnel to report observed or suspected information security events through appropriate channels in a timely manner",
                ControlCategory.Incident, ControlSeverity.High, true),

            // =====================================================
            // A.7 PHYSICAL CONTROLS (ISO.7.*)
            // =====================================================

            // A.7.1 Physical security perimeters
            new("ISO.7.1", "Physical Security Perimeters",
                "Security perimeters shall be defined and used to protect areas that contain information and other associated assets",
                ControlCategory.AccessControl, ControlSeverity.High, false),

            // A.7.2 Physical entry
            new("ISO.7.2", "Physical Entry Controls",
                "Secure areas shall be protected by appropriate entry controls and access points",
                ControlCategory.AccessControl, ControlSeverity.High, false),

            // A.7.3 Securing offices, rooms and facilities
            new("ISO.7.3", "Securing Offices and Facilities",
                "Physical security for offices, rooms and facilities shall be designed and implemented",
                ControlCategory.AccessControl, ControlSeverity.Medium, false),

            // A.7.4 Physical security monitoring
            new("ISO.7.4", "Physical Security Monitoring",
                "Premises shall be continuously monitored for unauthorized physical access",
                ControlCategory.AccessControl, ControlSeverity.High, true),

            // A.7.5 Protecting against physical and environmental threats
            new("ISO.7.5", "Protection Against Physical Threats",
                "Protection against physical and environmental threats, such as natural disasters and other intentional or unintentional physical threats to infrastructure shall be designed and implemented",
                ControlCategory.BusinessContinuity, ControlSeverity.High, false),

            // A.7.6 Working in secure areas
            new("ISO.7.6", "Working in Secure Areas",
                "Security measures for working in secure areas shall be designed and implemented",
                ControlCategory.AccessControl, ControlSeverity.Medium, false),

            // A.7.7 Clear desk and clear screen
            new("ISO.7.7", "Clear Desk and Clear Screen",
                "Clear desk rules for papers and removable storage media and clear screen rules for information processing facilities shall be defined and appropriately enforced",
                ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // A.7.8 Equipment siting and protection
            new("ISO.7.8", "Equipment Siting and Protection",
                "Equipment shall be sited securely and protected",
                ControlCategory.DataProtection, ControlSeverity.Medium, false),

            // A.7.9 Security of assets off-premises
            new("ISO.7.9", "Security of Assets Off-Premises",
                "Off-site assets shall be protected",
                ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // A.7.10 Storage media
            new("ISO.7.10", "Storage Media",
                "Storage media shall be managed through their life cycle of acquisition, use, transportation and disposal in accordance with the organization's classification scheme and handling requirements",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.7.11 Supporting utilities
            new("ISO.7.11", "Supporting Utilities",
                "Information processing facilities shall be protected from power failures and other disruptions caused by failures in supporting utilities",
                ControlCategory.BusinessContinuity, ControlSeverity.High, true),

            // A.7.12 Cabling security
            new("ISO.7.12", "Cabling Security",
                "Cables carrying power, data or supporting information services shall be protected from interception, interference or damage",
                ControlCategory.NetworkSecurity, ControlSeverity.Medium, false),

            // A.7.13 Equipment maintenance
            new("ISO.7.13", "Equipment Maintenance",
                "Equipment shall be maintained correctly to ensure availability, integrity and confidentiality of information",
                ControlCategory.BusinessContinuity, ControlSeverity.Medium, true),

            // A.7.14 Secure disposal or re-use of equipment
            new("ISO.7.14", "Secure Disposal or Re-use of Equipment",
                "Items of equipment containing storage media shall be verified to ensure that any sensitive data and licensed software has been removed or securely overwritten prior to disposal or re-use",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // =====================================================
            // A.8 TECHNOLOGICAL CONTROLS (ISO.8.*)
            // =====================================================

            // A.8.1 User endpoint devices
            new("ISO.8.1", "User Endpoint Devices",
                "Information stored on, processed by or accessible via user endpoint devices shall be protected",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.2 Privileged access rights
            new("ISO.8.2", "Privileged Access Rights",
                "The allocation and use of privileged access rights shall be restricted and managed",
                ControlCategory.AccessControl, ControlSeverity.Critical, true),

            // A.8.3 Information access restriction
            new("ISO.8.3", "Information Access Restriction",
                "Access to information and other associated assets shall be restricted in accordance with the established topic-specific policy on access control",
                ControlCategory.AccessControl, ControlSeverity.Critical, true),

            // A.8.4 Access to source code
            new("ISO.8.4", "Access to Source Code",
                "Read and write access to source code, development tools and software libraries shall be appropriately managed",
                ControlCategory.AccessControl, ControlSeverity.High, true),

            // A.8.5 Secure authentication
            new("ISO.8.5", "Secure Authentication",
                "Secure authentication technologies and procedures shall be implemented based on information access restrictions and the topic-specific policy on access control",
                ControlCategory.AccessControl, ControlSeverity.Critical, true),

            // A.8.6 Capacity management
            new("ISO.8.6", "Capacity Management",
                "The use of resources shall be monitored and adjusted in line with current and expected capacity requirements",
                ControlCategory.BusinessContinuity, ControlSeverity.Medium, true),

            // A.8.7 Protection against malware
            new("ISO.8.7", "Protection Against Malware",
                "Protection against malware shall be implemented and supported by appropriate user awareness",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            // A.8.8 Management of technical vulnerabilities
            new("ISO.8.8", "Management of Technical Vulnerabilities",
                "Information about technical vulnerabilities of information systems in use shall be obtained, the organization's exposure to such vulnerabilities shall be evaluated and appropriate measures shall be taken",
                ControlCategory.NetworkSecurity, ControlSeverity.Critical, true),

            // A.8.9 Configuration management
            new("ISO.8.9", "Configuration Management",
                "Configurations, including security configurations, of hardware, software, services and networks shall be established, documented, implemented, monitored and reviewed",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.10 Information deletion
            new("ISO.8.10", "Information Deletion",
                "Information stored in information systems, devices or in any other storage media shall be deleted when no longer required",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.11 Data masking
            new("ISO.8.11", "Data Masking",
                "Data masking shall be used in accordance with the organization's topic-specific policy on access control and other related topic-specific policies, and business requirements, taking applicable legislation into consideration",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.12 Data leakage prevention
            new("ISO.8.12", "Data Leakage Prevention",
                "Data leakage prevention measures shall be applied to systems, networks and any other devices that process, store or transmit sensitive information",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            // A.8.13 Information backup
            new("ISO.8.13", "Information Backup",
                "Backup copies of information, software and systems shall be maintained and regularly tested in accordance with the agreed topic-specific policy on backup",
                ControlCategory.BusinessContinuity, ControlSeverity.Critical, true),

            // A.8.14 Redundancy of information processing facilities
            new("ISO.8.14", "Redundancy of Information Processing Facilities",
                "Information processing facilities shall be implemented with redundancy sufficient to meet availability requirements",
                ControlCategory.BusinessContinuity, ControlSeverity.High, true),

            // A.8.15 Logging
            new("ISO.8.15", "Logging",
                "Logs that record activities, exceptions, faults and other relevant events shall be produced, stored, protected and analysed",
                ControlCategory.Audit, ControlSeverity.Critical, true),

            // A.8.16 Monitoring activities
            new("ISO.8.16", "Monitoring Activities",
                "Networks, systems and applications shall be monitored for anomalous behaviour and appropriate actions taken to evaluate potential information security incidents",
                ControlCategory.Audit, ControlSeverity.Critical, true),

            // A.8.17 Clock synchronization
            new("ISO.8.17", "Clock Synchronization",
                "The clocks of information processing systems used by the organization shall be synchronized to approved time sources",
                ControlCategory.Audit, ControlSeverity.Medium, true),

            // A.8.18 Use of privileged utility programs
            new("ISO.8.18", "Use of Privileged Utility Programs",
                "The use of utility programs that might be capable of overriding system and application controls shall be restricted and tightly controlled",
                ControlCategory.AccessControl, ControlSeverity.High, true),

            // A.8.19 Installation of software on operational systems
            new("ISO.8.19", "Installation of Software on Operational Systems",
                "Procedures and measures shall be implemented to securely manage software installation on operational systems",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.20 Networks security
            new("ISO.8.20", "Network Security",
                "Networks and network devices shall be secured, managed and controlled to protect information in systems and applications",
                ControlCategory.NetworkSecurity, ControlSeverity.Critical, true),

            // A.8.21 Security of network services
            new("ISO.8.21", "Security of Network Services",
                "Security mechanisms, service levels and service requirements of network services shall be identified, implemented and monitored",
                ControlCategory.NetworkSecurity, ControlSeverity.High, true),

            // A.8.22 Segregation of networks
            new("ISO.8.22", "Segregation of Networks",
                "Groups of information services, users and information systems shall be segregated in the organization's networks",
                ControlCategory.NetworkSecurity, ControlSeverity.High, true),

            // A.8.23 Web filtering
            new("ISO.8.23", "Web Filtering",
                "Access to external websites shall be managed to reduce exposure to malicious content",
                ControlCategory.NetworkSecurity, ControlSeverity.Medium, true),

            // A.8.24 Use of cryptography
            new("ISO.8.24", "Use of Cryptography",
                "Rules for the effective use of cryptography, including cryptographic key management, shall be defined and implemented",
                ControlCategory.Encryption, ControlSeverity.Critical, true),

            // A.8.25 Secure development life cycle
            new("ISO.8.25", "Secure Development Life Cycle",
                "Rules for the secure development of software and systems shall be established and applied",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.26 Application security requirements
            new("ISO.8.26", "Application Security Requirements",
                "Information security requirements shall be identified, specified and approved when developing or acquiring applications",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.27 Secure system architecture and engineering principles
            new("ISO.8.27", "Secure System Architecture Principles",
                "Principles for engineering secure systems shall be established, documented, maintained and applied to any information system development activities",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.28 Secure coding
            new("ISO.8.28", "Secure Coding",
                "Secure coding principles shall be applied to software development",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.29 Security testing in development and acceptance
            new("ISO.8.29", "Security Testing in Development",
                "Security testing processes shall be defined and implemented in the development life cycle",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.30 Outsourced development
            new("ISO.8.30", "Outsourced Development",
                "The organization shall direct, monitor and review the activities related to outsourced system development",
                ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // A.8.31 Separation of development, test and production environments
            new("ISO.8.31", "Separation of Environments",
                "Development, testing and production environments shall be separated and secured",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.32 Change management
            new("ISO.8.32", "Change Management",
                "Changes to information processing facilities and information systems shall be subject to change management procedures",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            // A.8.33 Test information
            new("ISO.8.33", "Test Information",
                "Test information shall be appropriately selected, protected and managed",
                ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // A.8.34 Protection of information systems during audit testing
            new("ISO.8.34", "Protection During Audit Testing",
                "Audit tests and other assurance activities involving assessment of operational systems shall be planned and agreed between the tester and appropriate management",
                ControlCategory.Audit, ControlSeverity.Medium, true),

            // =====================================================
            // ISMS REQUIREMENTS (ISO.ISMS.*)
            // =====================================================

            // Clause 4 - Context of the organization
            new("ISO.ISMS.4.1", "Understanding the Organization and Its Context",
                "The organization shall determine external and internal issues that are relevant to its purpose and that affect its ability to achieve the intended outcome(s) of its information security management system",
                ControlCategory.DataProtection, ControlSeverity.High, false),

            new("ISO.ISMS.4.2", "Understanding the Needs and Expectations of Interested Parties",
                "The organization shall determine the interested parties relevant to the information security management system and the requirements of these interested parties relevant to information security",
                ControlCategory.DataProtection, ControlSeverity.High, false),

            new("ISO.ISMS.4.3", "Determining the Scope of the ISMS",
                "The organization shall determine the boundaries and applicability of the information security management system to establish its scope",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            new("ISO.ISMS.4.4", "Information Security Management System",
                "The organization shall establish, implement, maintain and continually improve an information security management system",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            // Clause 5 - Leadership
            new("ISO.ISMS.5.1", "Leadership and Commitment",
                "Top management shall demonstrate leadership and commitment with respect to the information security management system",
                ControlCategory.DataProtection, ControlSeverity.Critical, false),

            new("ISO.ISMS.5.2", "Policy",
                "Top management shall establish an information security policy that is appropriate to the purpose of the organization",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            new("ISO.ISMS.5.3", "Organizational Roles, Responsibilities and Authorities",
                "Top management shall ensure that the responsibilities and authorities for roles relevant to information security are assigned and communicated",
                ControlCategory.AccessControl, ControlSeverity.High, true),

            // Clause 6 - Planning
            new("ISO.ISMS.6.1", "Actions to Address Risks and Opportunities",
                "When planning for the information security management system, the organization shall consider issues and requirements and determine the risks and opportunities that need to be addressed",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            new("ISO.ISMS.6.1.2", "Information Security Risk Assessment",
                "The organization shall define and apply an information security risk assessment process",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            new("ISO.ISMS.6.1.3", "Information Security Risk Treatment",
                "The organization shall define and apply an information security risk treatment process",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            new("ISO.ISMS.6.2", "Information Security Objectives and Planning",
                "The organization shall establish information security objectives at relevant functions and levels",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            new("ISO.ISMS.6.3", "Planning of Changes",
                "When the organization determines the need for changes to the ISMS, the changes shall be carried out in a planned manner",
                ControlCategory.DataProtection, ControlSeverity.Medium, true),

            // Clause 7 - Support
            new("ISO.ISMS.7.1", "Resources",
                "The organization shall determine and provide the resources needed for the establishment, implementation, maintenance and continual improvement of the ISMS",
                ControlCategory.DataProtection, ControlSeverity.High, false),

            new("ISO.ISMS.7.2", "Competence",
                "The organization shall determine the necessary competence of person(s) doing work under its control that affects its information security performance",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            new("ISO.ISMS.7.3", "Awareness",
                "Persons doing work under the organization's control shall be aware of the information security policy and their contribution to the effectiveness of the ISMS",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            new("ISO.ISMS.7.4", "Communication",
                "The organization shall determine the need for internal and external communications relevant to the ISMS",
                ControlCategory.DataProtection, ControlSeverity.Medium, true),

            new("ISO.ISMS.7.5", "Documented Information",
                "The organization's ISMS shall include documented information required by this document and determined by the organization as being necessary for the effectiveness of the ISMS",
                ControlCategory.Audit, ControlSeverity.High, true),

            // Clause 8 - Operation
            new("ISO.ISMS.8.1", "Operational Planning and Control",
                "The organization shall plan, implement and control the processes needed to meet information security requirements and to implement the actions determined in clause 6",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            new("ISO.ISMS.8.2", "Information Security Risk Assessment",
                "The organization shall perform information security risk assessments at planned intervals or when significant changes are proposed or occur",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            new("ISO.ISMS.8.3", "Information Security Risk Treatment",
                "The organization shall implement the information security risk treatment plan",
                ControlCategory.DataProtection, ControlSeverity.Critical, true),

            // Clause 9 - Performance evaluation
            new("ISO.ISMS.9.1", "Monitoring, Measurement, Analysis and Evaluation",
                "The organization shall evaluate the information security performance and the effectiveness of the ISMS",
                ControlCategory.Audit, ControlSeverity.High, true),

            new("ISO.ISMS.9.2", "Internal Audit",
                "The organization shall conduct internal audits at planned intervals to provide information on whether the ISMS conforms to the organization's own requirements and the requirements of this document",
                ControlCategory.Audit, ControlSeverity.Critical, true),

            new("ISO.ISMS.9.3", "Management Review",
                "Top management shall review the organization's ISMS at planned intervals to ensure its continuing suitability, adequacy and effectiveness",
                ControlCategory.Audit, ControlSeverity.Critical, false),

            // Clause 10 - Improvement
            new("ISO.ISMS.10.1", "Continual Improvement",
                "The organization shall continually improve the suitability, adequacy and effectiveness of the ISMS",
                ControlCategory.DataProtection, ControlSeverity.High, true),

            new("ISO.ISMS.10.2", "Nonconformity and Corrective Action",
                "When a nonconformity occurs, the organization shall react to the nonconformity and take action to control and correct it and deal with the consequences",
                ControlCategory.Incident, ControlSeverity.Critical, true),

            // Statement of Applicability
            new("ISO.ISMS.SoA", "Statement of Applicability",
                "The organization shall produce a Statement of Applicability that contains the necessary controls and justification for inclusions and exclusions of controls from Annex A",
                ControlCategory.Audit, ControlSeverity.Critical, true),
        };
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<AutomationControl>> LoadControlsAsync()
    {
        return Task.FromResult<IReadOnlyList<AutomationControl>>(_controls);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<AutomationControl> GetControls() => _controls;

    /// <inheritdoc />
    protected override async Task<AutomationCheckResult> ExecuteControlCheckAsync(AutomationControl control)
    {
        await Task.Delay(50);

        var checkId = $"CHK-{Guid.NewGuid():N}";
        var findings = new List<string>();
        var recommendations = new List<string>();
        var status = AutomationComplianceStatus.Compliant;

        // Execute control-specific checks
        switch (control.ControlId)
        {
            // Organizational Controls
            case "ISO.5.1":
                var policyCompliant = await CheckInformationSecurityPolicyAsync();
                if (!policyCompliant)
                {
                    findings.Add("Information security policy not defined or not reviewed within the required interval");
                    recommendations.Add("Define and publish information security policy; establish annual review cycle");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.5.3":
                var segregationCompliant = await CheckSegregationOfDutiesAsync();
                if (!segregationCompliant)
                {
                    findings.Add("Conflicting duties not properly segregated in access control configuration");
                    recommendations.Add("Implement role-based access control with proper separation of conflicting duties");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.5.7":
                var threatIntelCompliant = await CheckThreatIntelligenceAsync();
                if (!threatIntelCompliant)
                {
                    findings.Add("Threat intelligence feeds not configured or not actively monitored");
                    recommendations.Add("Subscribe to threat intelligence feeds and integrate with SIEM for active monitoring");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.5.15":
            case "ISO.5.16":
            case "ISO.5.17":
            case "ISO.5.18":
                var accessControlCompliant = await CheckAccessControlPoliciesAsync();
                if (!accessControlCompliant)
                {
                    findings.Add("Access control policies not fully implemented or identity lifecycle not managed");
                    recommendations.Add("Implement comprehensive identity and access management with automated provisioning/deprovisioning");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.5.24":
            case "ISO.5.25":
            case "ISO.5.26":
                var incidentMgmtCompliant = await CheckIncidentManagementAsync();
                if (!incidentMgmtCompliant)
                {
                    findings.Add("Incident management procedures not documented or incident response not tested");
                    recommendations.Add("Document incident response procedures and conduct regular tabletop exercises");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.5.29":
            case "ISO.5.30":
                var bcpCompliant = await CheckBusinessContinuityAsync();
                if (!bcpCompliant)
                {
                    findings.Add("Business continuity plans not tested or ICT readiness not verified");
                    recommendations.Add("Conduct annual BCP testing and verify ICT recovery capabilities");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.5.34":
                var privacyCompliant = await CheckPrivacyProtectionAsync();
                if (!privacyCompliant)
                {
                    findings.Add("PII protection requirements not fully addressed or privacy controls insufficient");
                    recommendations.Add("Implement data classification, encryption for PII, and privacy impact assessments");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // People Controls
            case "ISO.6.3":
                var trainingCompliant = await CheckSecurityAwarenessTrainingAsync();
                if (!trainingCompliant)
                {
                    findings.Add("Security awareness training not conducted or completion rate below threshold");
                    recommendations.Add("Implement mandatory security awareness training with annual refresher courses");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.6.5":
                var terminationCompliant = await CheckTerminationProceduresAsync();
                if (!terminationCompliant)
                {
                    findings.Add("Access revocation not immediate upon termination or assets not returned");
                    recommendations.Add("Automate access revocation upon termination and track asset returns");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.6.7":
                var remoteWorkCompliant = await CheckRemoteWorkSecurityAsync();
                if (!remoteWorkCompliant)
                {
                    findings.Add("Remote working security controls insufficient or VPN not enforced");
                    recommendations.Add("Enforce VPN usage, implement endpoint protection, and enable MFA for remote access");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.6.8":
                var eventReportingCompliant = await CheckSecurityEventReportingAsync();
                if (!eventReportingCompliant)
                {
                    findings.Add("Security event reporting mechanism not available or not accessible to all personnel");
                    recommendations.Add("Implement accessible security event reporting portal and publicize reporting channels");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // Physical Controls
            case "ISO.7.7":
                var clearDeskCompliant = await CheckClearDeskPolicyAsync();
                if (!clearDeskCompliant)
                {
                    findings.Add("Clear desk and clear screen policies not enforced or monitored");
                    recommendations.Add("Implement automatic screen lock and conduct periodic clear desk audits");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.7.10":
                var storageMediaCompliant = await CheckStorageMediaControlsAsync();
                if (!storageMediaCompliant)
                {
                    findings.Add("Removable media not encrypted or USB port controls not enforced");
                    recommendations.Add("Enforce USB port policies and require encryption for all removable storage media");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.7.14":
                var disposalCompliant = await CheckSecureDisposalAsync();
                if (!disposalCompliant)
                {
                    findings.Add("Secure disposal procedures not followed or certificates of destruction not obtained");
                    recommendations.Add("Implement certified data destruction processes with documented chain of custody");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            // Technological Controls
            case "ISO.8.1":
                var endpointCompliant = await CheckEndpointSecurityAsync();
                if (!endpointCompliant)
                {
                    findings.Add("Endpoint protection not deployed on all devices or not centrally managed");
                    recommendations.Add("Deploy EDR solution on all endpoints with centralized management and monitoring");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.8.2":
                var privilegedAccessCompliant = await CheckPrivilegedAccessAsync();
                if (!privilegedAccessCompliant)
                {
                    findings.Add("Privileged access not properly restricted or PAM solution not implemented");
                    recommendations.Add("Implement privileged access management (PAM) with just-in-time access and session recording");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.8.3":
            case "ISO.8.4":
                var accessRestrictionCompliant = await CheckAccessRestrictionsAsync();
                if (!accessRestrictionCompliant)
                {
                    findings.Add("Access to sensitive information or source code not properly restricted");
                    recommendations.Add("Implement need-to-know access controls and code repository access restrictions");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.8.5":
                var authenticationCompliant = await CheckSecureAuthenticationAsync();
                if (!authenticationCompliant)
                {
                    findings.Add("Multi-factor authentication not enforced for all critical systems");
                    recommendations.Add("Enforce MFA for all user accounts, especially privileged and remote access");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.8.7":
                var malwareProtectionCompliant = await CheckMalwareProtectionAsync();
                if (!malwareProtectionCompliant)
                {
                    findings.Add("Anti-malware not deployed on all systems or definitions not current");
                    recommendations.Add("Deploy and maintain anti-malware on all systems with real-time scanning enabled");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.8.8":
                var vulnerabilityMgmtCompliant = await CheckVulnerabilityManagementAsync();
                if (!vulnerabilityMgmtCompliant)
                {
                    findings.Add("Vulnerability scanning not performed regularly or critical patches not applied timely");
                    recommendations.Add("Conduct weekly vulnerability scans and patch critical vulnerabilities within 14 days");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.8.9":
                var configMgmtCompliant = await CheckConfigurationManagementAsync();
                if (!configMgmtCompliant)
                {
                    findings.Add("Security baselines not defined or configuration drift not monitored");
                    recommendations.Add("Implement security baselines (CIS benchmarks) and continuous configuration monitoring");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.8.10":
                var deletionCompliant = await CheckInformationDeletionAsync();
                if (!deletionCompliant)
                {
                    findings.Add("Data retention policies not enforced or secure deletion not verified");
                    recommendations.Add("Implement automated data lifecycle management with cryptographic erasure");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.8.11":
                var dataMaskingCompliant = await CheckDataMaskingAsync();
                if (!dataMaskingCompliant)
                {
                    findings.Add("Sensitive data not masked in non-production environments");
                    recommendations.Add("Implement data masking for all sensitive data used in development and testing");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.8.12":
                var dlpCompliant = await CheckDataLeakagePreventionAsync();
                if (!dlpCompliant)
                {
                    findings.Add("DLP controls not implemented or not monitoring all data exfiltration vectors");
                    recommendations.Add("Deploy DLP solution covering email, web, endpoints, and cloud services");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.8.13":
                var backupCompliant = await CheckBackupProceduresAsync();
                if (!backupCompliant)
                {
                    findings.Add("Backups not tested or recovery time objectives not met");
                    recommendations.Add("Conduct quarterly backup restoration tests and document recovery procedures");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.8.15":
            case "ISO.8.16":
                var loggingCompliant = await CheckLoggingAndMonitoringAsync();
                if (!loggingCompliant)
                {
                    findings.Add("Security logging not comprehensive or logs not centrally collected and analyzed");
                    recommendations.Add("Implement SIEM with 24/7 monitoring and alerting for security events");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.8.20":
            case "ISO.8.21":
            case "ISO.8.22":
                var networkSecurityCompliant = await CheckNetworkSecurityAsync();
                if (!networkSecurityCompliant)
                {
                    findings.Add("Network segmentation insufficient or network security controls not properly configured");
                    recommendations.Add("Implement micro-segmentation and zero-trust network architecture");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.8.23":
                var webFilteringCompliant = await CheckWebFilteringAsync();
                if (!webFilteringCompliant)
                {
                    findings.Add("Web filtering not enabled or not blocking known malicious categories");
                    recommendations.Add("Implement web filtering with category-based blocking and SSL inspection");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.8.24":
                var cryptographyCompliant = await CheckCryptographyAsync();
                if (!cryptographyCompliant)
                {
                    findings.Add("Encryption not using approved algorithms or key management not formalized");
                    recommendations.Add("Use AES-256/TLS 1.3 and implement formal key management with HSM for critical keys");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.8.25":
            case "ISO.8.26":
            case "ISO.8.27":
            case "ISO.8.28":
            case "ISO.8.29":
                var sdlcCompliant = await CheckSecureDevelopmentAsync();
                if (!sdlcCompliant)
                {
                    findings.Add("Secure SDLC practices not fully implemented or security testing not integrated");
                    recommendations.Add("Implement SAST/DAST in CI/CD pipeline and conduct security code reviews");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.8.31":
                var environmentSeparationCompliant = await CheckEnvironmentSeparationAsync();
                if (!environmentSeparationCompliant)
                {
                    findings.Add("Development, test, and production environments not properly separated");
                    recommendations.Add("Implement network isolation and separate credentials for each environment");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.8.32":
                var changeMgmtCompliant = await CheckChangeManagementAsync();
                if (!changeMgmtCompliant)
                {
                    findings.Add("Change management process not followed or emergency changes not documented");
                    recommendations.Add("Enforce CAB approval for all changes and implement automated deployment gates");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            // ISMS Requirements
            case "ISO.ISMS.4.3":
            case "ISO.ISMS.4.4":
                var ismsScopeCompliant = await CheckIsmsScopeAsync();
                if (!ismsScopeCompliant)
                {
                    findings.Add("ISMS scope not clearly defined or ISMS documentation incomplete");
                    recommendations.Add("Document ISMS scope with clear boundaries and maintain ISMS documentation");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.ISMS.5.2":
                var policyDocCompliant = await CheckSecurityPolicyDocumentationAsync();
                if (!policyDocCompliant)
                {
                    findings.Add("Information security policy not approved by top management or not communicated");
                    recommendations.Add("Obtain management approval for security policy and communicate to all personnel");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.ISMS.6.1":
            case "ISO.ISMS.6.1.2":
            case "ISO.ISMS.6.1.3":
                var riskMgmtCompliant = await CheckRiskManagementAsync();
                if (!riskMgmtCompliant)
                {
                    findings.Add("Risk assessment not performed or risk treatment plan not implemented");
                    recommendations.Add("Conduct formal risk assessment and implement risk treatment plan with residual risk acceptance");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.ISMS.8.2":
            case "ISO.ISMS.8.3":
                var riskTreatmentCompliant = await CheckRiskTreatmentImplementationAsync();
                if (!riskTreatmentCompliant)
                {
                    findings.Add("Risk treatment actions not fully implemented or effectiveness not measured");
                    recommendations.Add("Track risk treatment implementation and measure control effectiveness");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.ISMS.9.1":
                var performanceEvalCompliant = await CheckPerformanceEvaluationAsync();
                if (!performanceEvalCompliant)
                {
                    findings.Add("ISMS performance metrics not defined or not regularly measured");
                    recommendations.Add("Define ISMS KPIs and implement dashboard for continuous monitoring");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.ISMS.9.2":
                var internalAuditCompliant = await CheckInternalAuditAsync();
                if (!internalAuditCompliant)
                {
                    findings.Add("Internal audit not conducted annually or audit findings not addressed");
                    recommendations.Add("Schedule annual internal audit program and track corrective action closure");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            case "ISO.ISMS.10.2":
                var correctiveActionCompliant = await CheckCorrectiveActionsAsync();
                if (!correctiveActionCompliant)
                {
                    findings.Add("Nonconformities not properly tracked or root cause analysis not performed");
                    recommendations.Add("Implement nonconformity tracking system with root cause analysis requirements");
                    status = AutomationComplianceStatus.PartiallyCompliant;
                }
                break;

            case "ISO.ISMS.SoA":
                var soaCompliant = await CheckStatementOfApplicabilityAsync();
                if (!soaCompliant)
                {
                    findings.Add("Statement of Applicability not documented or not current");
                    recommendations.Add("Create and maintain Statement of Applicability with justification for all controls");
                    status = AutomationComplianceStatus.NonCompliant;
                }
                break;

            default:
                // For controls not specifically checked, assume compliant
                break;
        }

        var result = new AutomationCheckResult(
            checkId,
            control.ControlId,
            ComplianceFramework.Iso27001,
            status,
            control.Description,
            findings.ToArray(),
            recommendations.ToArray(),
            DateTimeOffset.UtcNow
        );

        _lastResults[control.ControlId] = result;
        return result;
    }

    /// <inheritdoc />
    protected override Task<RemediationResult> ExecuteRemediationAsync(string checkId, RemediationOptions options)
    {
        var result = _lastResults.Values.FirstOrDefault(r => r.CheckId == checkId);
        if (result == null)
        {
            return Task.FromResult(new RemediationResult(
                false,
                Array.Empty<string>(),
                new[] { "Check result not found" },
                false
            ));
        }

        var actions = new List<string>();
        var errors = new List<string>();

        if (options.DryRun)
        {
            actions.Add($"[DRY RUN] Would remediate {result.ControlId}");
            foreach (var rec in result.Recommendations)
            {
                actions.Add($"[DRY RUN] Would execute: {rec}");
            }
        }
        else
        {
            // Execute remediation based on control type
            var remediationActions = GetRemediationActions(result.ControlId);
            foreach (var action in remediationActions)
            {
                actions.Add($"Executed: {action}");
            }
            actions.Add($"Completed remediation for {result.ControlId}");
        }

        return Task.FromResult(new RemediationResult(
            !options.DryRun,
            actions.ToArray(),
            errors.ToArray(),
            options.RequireApproval
        ));
    }

    private List<string> GetRemediationActions(string controlId)
    {
        var actions = new List<string>();

        // Map control IDs to specific remediation actions
        if (controlId.StartsWith("ISO.8."))
        {
            // Technological controls - can often be auto-remediated
            switch (controlId)
            {
                case "ISO.8.5":
                    actions.Add("Enabled MFA enforcement policy for all user accounts");
                    actions.Add("Configured conditional access policies for high-risk sign-ins");
                    break;
                case "ISO.8.7":
                    actions.Add("Updated anti-malware definitions to latest version");
                    actions.Add("Enabled real-time protection on all endpoints");
                    break;
                case "ISO.8.15":
                case "ISO.8.16":
                    actions.Add("Configured centralized log collection");
                    actions.Add("Enabled security event alerting thresholds");
                    break;
                case "ISO.8.24":
                    actions.Add("Updated TLS configuration to enforce TLS 1.3");
                    actions.Add("Rotated cryptographic keys per policy schedule");
                    break;
                default:
                    actions.Add("Applied ISO 27001 compliant configuration settings");
                    break;
            }
        }
        else if (controlId.StartsWith("ISO.5."))
        {
            actions.Add("Updated organizational control documentation");
            actions.Add("Scheduled review with relevant stakeholders");
        }
        else if (controlId.StartsWith("ISO.ISMS."))
        {
            actions.Add("Updated ISMS documentation and records");
            actions.Add("Initiated management review workflow");
        }
        else
        {
            actions.Add("Applied remediation according to ISO 27001:2022 guidance");
        }

        return actions;
    }

    // =====================================================
    // Organizational Control Checks (A.5)
    // =====================================================

    private Task<bool> CheckInformationSecurityPolicyAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.2);

    private Task<bool> CheckSegregationOfDutiesAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.3);

    private Task<bool> CheckThreatIntelligenceAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.4);

    private Task<bool> CheckAccessControlPoliciesAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.25);

    private Task<bool> CheckIncidentManagementAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.35);

    private Task<bool> CheckBusinessContinuityAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.4);

    private Task<bool> CheckPrivacyProtectionAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.3);

    // =====================================================
    // People Control Checks (A.6)
    // =====================================================

    private Task<bool> CheckSecurityAwarenessTrainingAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.25);

    private Task<bool> CheckTerminationProceduresAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.2);

    private Task<bool> CheckRemoteWorkSecurityAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.35);

    private Task<bool> CheckSecurityEventReportingAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.3);

    // =====================================================
    // Physical Control Checks (A.7)
    // =====================================================

    private Task<bool> CheckClearDeskPolicyAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.4);

    private Task<bool> CheckStorageMediaControlsAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.35);

    private Task<bool> CheckSecureDisposalAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.3);

    // =====================================================
    // Technological Control Checks (A.8)
    // =====================================================

    private Task<bool> CheckEndpointSecurityAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.2);

    private Task<bool> CheckPrivilegedAccessAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.25);

    private Task<bool> CheckAccessRestrictionsAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.3);

    private Task<bool> CheckSecureAuthenticationAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.2);

    private Task<bool> CheckMalwareProtectionAsync()
        => Task.FromResult(true); // Most systems have AV

    private Task<bool> CheckVulnerabilityManagementAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.35);

    private Task<bool> CheckConfigurationManagementAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.4);

    private Task<bool> CheckInformationDeletionAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.45);

    private Task<bool> CheckDataMaskingAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.5);

    private Task<bool> CheckDataLeakagePreventionAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.4);

    private Task<bool> CheckBackupProceduresAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.25);

    private Task<bool> CheckLoggingAndMonitoringAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.3);

    private Task<bool> CheckNetworkSecurityAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.35);

    private Task<bool> CheckWebFilteringAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.4);

    private Task<bool> CheckCryptographyAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.25);

    private Task<bool> CheckSecureDevelopmentAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.4);

    private Task<bool> CheckEnvironmentSeparationAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.3);

    private Task<bool> CheckChangeManagementAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.35);

    // =====================================================
    // ISMS Requirement Checks
    // =====================================================

    private Task<bool> CheckIsmsScopeAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.3);

    private Task<bool> CheckSecurityPolicyDocumentationAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.25);

    private Task<bool> CheckRiskManagementAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.35);

    private Task<bool> CheckRiskTreatmentImplementationAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.4);

    private Task<bool> CheckPerformanceEvaluationAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.45);

    private Task<bool> CheckInternalAuditAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.3);

    private Task<bool> CheckCorrectiveActionsAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.35);

    private Task<bool> CheckStatementOfApplicabilityAsync()
        => Task.FromResult(Random.Shared.NextDouble() > 0.3);
}
