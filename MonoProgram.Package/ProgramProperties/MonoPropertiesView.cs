﻿using System.Runtime.InteropServices;
using MonoProgram.Package.PropertyPages;

namespace MonoProgram.Package.ProgramProperties
{
    [Guid("3DE15C6B-2C7F-416C-B496-7D76A64A5247")]
    public partial class MonoPropertiesView : PageView
    {
        private PropertyControlTable propertyControlTable;

        public MonoPropertiesView()
        {
            InitializeComponent();
        }

        public MonoPropertiesView(IPageViewSite pageViewSite) : base(pageViewSite)
        {
            InitializeComponent();
        }

        /// <summary>
        /// This property is used to map the control on a PageView object to a property
        /// in PropertyStore object.
        /// 
        /// This property will be called in the base class's constructor, which means that
        /// the InitializeComponent has not been called and the Controls have not been
        /// initialized.
        /// </summary>
		protected override PropertyControlTable PropertyControlTable
		{
			get
			{
				if (propertyControlTable == null)
				{
					// This is the list of properties that will be persisted and their
					// assciation to the controls.
					propertyControlTable = new PropertyControlTable();

                    // This means that this CustomPropertyPageView object has not been
                    // initialized.
                    if (string.IsNullOrEmpty(Name))
                    {
                        InitializeComponent();
                    }

                    // Add two Property Name / Control KeyValuePairs. 
					propertyControlTable.Add(MonoPropertyPage.DebugHostProperty, hostTextBox);
                    propertyControlTable.Add(MonoPropertyPage.DebugUsernameProperty, usernameTextBox);
				    propertyControlTable.Add(MonoPropertyPage.DebugPasswordProperty, passwordTextBox);
				    propertyControlTable.Add(MonoPropertyPage.DebugDestinationProperty, destinationTextBox);
				    propertyControlTable.Add(MonoPropertyPage.BuildHostProperty, buildServerTextBox);
				    propertyControlTable.Add(MonoPropertyPage.BuildFolderProperty, buildFolderTextBox);
				    propertyControlTable.Add(MonoPropertyPage.BuildUsernameProperty, buildUsername);
				    propertyControlTable.Add(MonoPropertyPage.BuildPasswordProperty, buildPassword);
				}
				return propertyControlTable;
			}
		}
    }
}
